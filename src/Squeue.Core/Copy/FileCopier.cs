using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Core.Copy;

/// Copies one journal entry following the per-file protocol:
/// intent → temp created → written + flushed → (verified, when replacing) → published + flushed → verified → finished.
/// Each phase is committed to the journal before the next filesystem change, so the Reconciler can always
/// tell what happened after a crash.
public sealed class FileCopier
{
    // Read-only, hidden, system, archive, not-content-indexed. Compression, encryption and sparse are not copied.
    private const uint CopiedAttributes = 0x1 | 0x2 | 0x4 | 0x20 | 0x2000;

    private readonly IFileSystem _fs;
    private readonly Journal _journal;
    private readonly CopyOptions _options;

    public FileCopier(IFileSystem fs, Journal journal, CopyOptions? options = null)
    {
        _options = options ?? new CopyOptions();
        if (_options.ChunkSize <= 0 || _options.ChunkSize % AlignedBuffer.Alignment != 0)
            throw new ArgumentException("Chunk size must be a positive multiple of 4096.", nameof(options));
        _fs = fs;
        _journal = journal;
    }

    public CopyResult CopyEntry(long entryId, IProgress<CopyProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var entry = _journal.GetEntry(entryId);
            if (entry.State == EntryState.Done) return new CopyResult(CopyOutcome.Done);

            var open = _journal.GetOpenAttempt(entryId);
            if (open is { Phase: AttemptPhase.Published or AttemptPhase.Verified })
                return FinishPublished(entry, open, progress, cancellationToken);
            if (open is not null)
                throw new InvalidOperationException($"Entry {entryId} has an unreconciled attempt. Run the Reconciler first.");

            cancellationToken.ThrowIfCancellationRequested();
            return StartAttempt(entry, progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // In-flight work is already cleaned up (temp deleted) or left resumable (published, not yet verified).
            return new CopyResult(CopyOutcome.Cancelled, "Stopped on request.");
        }
    }

    private CopyResult StartAttempt(Entry entry, IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        if (entry.HashAlgorithm is { } algorithm && !Hashers.Supported.Contains(algorithm))
            return Fail(entry, $"Unknown hash algorithm '{algorithm}'.");

        ISourceFile source;
        try { source = OpenSourceWithRetry(entry.SrcPath); }
        catch (FsException ex) when (ex.IsSharingViolation) { return Fail(entry, "The source is in use by another program.", error: ErrorCode(ex)); }
        catch (FsException ex) when (ex.IsNotFound) { return Fail(entry, "The source no longer exists.", error: ErrorCode(ex)); }
        catch (IOException ex) { return Fail(entry, ex.Message, error: ErrorCode(ex)); }

        using (source)
        {
            try { _fs.CreateDirectory(entry.DestDirectory); }
            catch (IOException ex) { return Fail(entry, ex.Message, error: ErrorCode(ex)); }
            catch (UnauthorizedAccessException ex) { return Fail(entry, ex.Message); }

            string tempName = TempNames.New();
            string tempPath = Path.Combine(entry.DestDirectory, tempName);
            UInt128? replaces = entry.Action == ConflictAction.Replace ? entry.SeenDest?.FileId : null;
            long attemptId = 0;
            // One commit for everything that must be durable before the temp file exists.
            _journal.Atomically(() =>
            {
                _journal.RecordSourceVersion(entry.Id, source.Identity);
                attemptId = _journal.BeginAttempt(entry.Id, tempName, replaces);
                _journal.ClearHashes(entry.Id); // hashes from an earlier attempt must never satisfy this one
                _journal.SetEntryState(entry.Id, EntryState.Active);
            });

            ITempFile temp;
            try { temp = _fs.CreateTemp(tempPath); }
            catch (IOException ex)
            {
                return Fail(entry, ex.Message, attemptId, ErrorCode(ex));
            }

            using (temp)
            {
                UInt128 tempId = temp.GetIdentity().FileId;
                _journal.SetPhase(attemptId, AttemptPhase.TempCreated, tempFileId: tempId);
                long size = source.Identity.Size;
                try
                {
                    string? srcHash = WriteTemp(source, temp, entry.HashAlgorithm, progress, cancellationToken);
                    _journal.Atomically(() =>
                    {
                        _journal.SetHashes(entry.Id, srcHash, null);
                        _journal.SetPhase(attemptId, AttemptPhase.Written);
                    });
                    source.Dispose(); // fully read; release it before publishing

                    if (entry.Action == ConflictAction.Replace)
                    {
                        // Never replace an existing file with one that hasn't passed verification.
                        if (srcHash is not null)
                        {
                            string tempHash = HashUnbuffered(tempPath, entry.HashAlgorithm!, size, progress, cancellationToken);
                            if (tempHash != srcHash)
                            {
                                Abandon(temp, tempPath, tempId);
                                return VerificationFailed(entry, attemptId, replaceTarget: null);
                            }
                            _journal.Atomically(() =>
                            {
                                _journal.SetHashes(entry.Id, null, tempHash);
                                _journal.SetPhase(attemptId, AttemptPhase.VerifiedTemp);
                            });
                        }

                        if (_fs.TryGetIdentity(entry.DestPath) is not { } current
                            || entry.SeenDest is not { } seen
                            || !current.IsSameVersion(seen))
                        {
                            return DestinationChanged(entry, temp, tempPath, tempId, attemptId);
                        }
                    }

                    try { temp.RenameTo(entry.DestPath, replaceExisting: entry.Action == ConflictAction.Replace); }
                    catch (FsException ex) when (ex.IsAlreadyExists) { return DestinationChanged(entry, temp, tempPath, tempId, attemptId); }
                }
                catch (OperationCanceledException)
                {
                    // Stopped before publishing: nothing reached the final name, so remove the temp and start over later.
                    Abandon(temp, tempPath, tempId);
                    _journal.Atomically(() =>
                    {
                        _journal.SetEntryState(entry.Id, EntryState.Pending);
                        _journal.SetPhase(attemptId, AttemptPhase.Abandoned);
                    });
                    throw;
                }
                catch (IOException ex)
                {
                    Abandon(temp, tempPath, tempId);
                    return Fail(entry, ex.Message, attemptId, ErrorCode(ex));
                }

                // Make the rename durable. A failure here propagates; reconciliation will find the published file.
                temp.Flush();
                // Read after the rename: some filesystems (FAT/exFAT) can change a file's id when it is renamed.
                _journal.SetPhase(attemptId, AttemptPhase.Published, publishedFileId: temp.GetIdentity().FileId);
            }
        }

        return FinishPublished(_journal.GetEntry(entry.Id), _journal.GetOpenAttempt(entry.Id)!, progress, cancellationToken);
    }

    private CopyResult FinishPublished(Entry entry, Attempt attempt, IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        // The file at the destination must still be the one this attempt published, whatever phase we resume from.
        var found = _fs.TryGetIdentity(entry.DestPath);
        // A disconnected drive says nothing about the published file: keep the attempt open for when it is back.
        if (found is null && !VolumeConnected(entry.DestPath))
            throw new FsException("verify", entry.DestPath, DeviceNotConnected);
        if (found is not { } current || current.FileId != attempt.PublishedFileId)
            return Fail(entry, "The destination changed after it was copied. The source was kept.", attempt.Id);

        string? verifiedHash = null;
        bool needsVerification = attempt.Phase == AttemptPhase.Published && entry.HashAlgorithm is not null && entry.DestHash is null;
        if (needsVerification)
        {
            // Cancelling here leaves the attempt Published: the next CopyEntry verifies it again.
            string destHash = HashUnbuffered(entry.DestPath, entry.HashAlgorithm!, current.Size, progress, cancellationToken);
            if (destHash != entry.SrcHash)
            {
                // The outcome and the attempt's close commit together, so a crash can't strand the entry.
                var published = _fs.TryGetIdentity(entry.DestPath);
                if (published is null && !VolumeConnected(entry.DestPath))
                    throw new FsException("verify", entry.DestPath, DeviceNotConnected);
                if (published is null)
                    return Fail(entry, "The copied file disappeared before it could be verified. The source was kept.", attempt.Id);
                if (published.Value.FileId != attempt.PublishedFileId)
                    return Fail(entry, "The destination changed after it was copied. The source was kept.", attempt.Id);
                return VerificationFailed(entry, attempt.Id, replaceTarget: published.Value);
            }
            verifiedHash = destHash;
        }

        _journal.Atomically(() =>
        {
            if (verifiedHash is not null) _journal.SetHashes(entry.Id, null, verifiedHash);
            _journal.SetPhase(attempt.Id, AttemptPhase.Finished);
            _journal.SetEntryState(entry.Id, EntryState.Done);
        });
        return new CopyResult(CopyOutcome.Done);
    }

    private string? WriteTemp(ISourceFile source, ITempFile temp, string? algorithm,
        IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        var hasher = algorithm is null ? null : Hashers.Create(algorithm);
        long total = source.Identity.Size;
        temp.Preallocate(total);

        var buffer = new byte[_options.ChunkSize];
        long offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = source.Read(buffer, offset);
            if (read == 0) break;
            var chunk = buffer.AsSpan(0, read);
            hasher?.Append(chunk);
            temp.Write(chunk, offset);
            offset += read;
            progress?.Report(new CopyProgress(CopyStage.Copying, offset, total));
        }

        temp.SetLength(offset);
        var s = source.Identity;
        temp.SetTimesAndAttributes(s.CreationTime, s.LastWriteTime, s.Attributes & CopiedAttributes);
        temp.Flush(); // data is on disk before the file can get its final name
        return hasher?.FinishHex();
    }

    private string HashUnbuffered(string path, string algorithm, long total,
        IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        var hasher = Hashers.Create(algorithm);
        using var file = _fs.OpenForVerify(path);
        using var buffer = new AlignedBuffer(_options.ChunkSize);
        long offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = file.Read(buffer.Span, offset);
            if (read == 0) break;
            hasher.Append(buffer.Span[..read]);
            offset += read;
            progress?.Report(new CopyProgress(CopyStage.Verifying, offset, total));
            if (read < buffer.Length) break;
        }
        return hasher.FinishHex();
    }

    private ISourceFile OpenSourceWithRetry(string path)
    {
        var deadline = DateTime.UtcNow + _options.SharingRetryTimeout;
        while (true)
        {
            try { return _fs.OpenSource(path); }
            catch (FsException ex) when (ex.IsSharingViolation && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(_options.SharingRetryDelay);
            }
        }
    }

    /// Closes our handle and deletes the temp file. The caller records the outcome and closes the attempt.
    private void Abandon(ITempFile temp, string tempPath, UInt128 tempId)
    {
        temp.Dispose(); // close our handle so the delete can open the file
        _fs.DeleteIfSameObject(tempPath, tempId);
    }

    private CopyResult DestinationChanged(Entry entry, ITempFile temp, string tempPath, UInt128 tempId, long attemptId)
    {
        Abandon(temp, tempPath, tempId);
        _journal.Atomically(() =>
        {
            _journal.SetEntryState(entry.Id, EntryState.Pending);
            _journal.SetPhase(attemptId, AttemptPhase.Abandoned);
        });
        return new CopyResult(CopyOutcome.DestinationChanged, "The destination changed after the conflict decision.");
    }

    private CopyResult VerificationFailed(Entry entry, long attemptId, FileIdentity? replaceTarget)
    {
        CopyResult? result = null;
        _journal.Atomically(() =>
        {
            int failures = _journal.RecordVerifyFailure(entry.Id, replaceTarget);
            if (failures >= 2)
            {
                result = Fail(entry, "Verification failed twice. The copy at the destination may be damaged; the source was kept.", attemptId);
                return;
            }
            _journal.SetEntryState(entry.Id, EntryState.Pending);
            _journal.SetPhase(attemptId, AttemptPhase.Abandoned);
            result = new CopyResult(CopyOutcome.RetryNeeded, "Verification failed. The file will be copied again.");
        });
        return result!;
    }

    private const int DeviceNotConnected = 1167;

    /// The Windows error code behind an I/O failure.
    private static int ErrorCode(IOException ex) => ex is FsException fs ? fs.Win32Error : ex.HResult & 0xFFFF;

    private static bool VolumeConnected(string path) => Path.GetPathRoot(path) is not { Length: > 0 } root || Directory.Exists(root);

    /// Fails the entry; when an attempt is given, it is closed in the same transaction.
    private CopyResult Fail(Entry entry, string message, long? attemptId = null, int error = 0)
    {
        _journal.Atomically(() =>
        {
            _journal.SetEntryState(entry.Id, EntryState.Failed, message);
            if (attemptId is { } id) _journal.SetPhase(id, AttemptPhase.Abandoned);
        });
        return new CopyResult(CopyOutcome.Failed, message, error);
    }
}
