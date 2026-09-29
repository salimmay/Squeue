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

    public CopyResult CopyEntry(long entryId)
    {
        var entry = _journal.GetEntry(entryId);
        if (entry.State == EntryState.Done) return new CopyResult(CopyOutcome.Done);

        var open = _journal.GetOpenAttempt(entryId);
        if (open is { Phase: AttemptPhase.Published or AttemptPhase.Verified }) return FinishPublished(entry, open);
        if (open is not null)
            throw new InvalidOperationException($"Entry {entryId} has an unreconciled attempt. Run the Reconciler first.");

        return StartAttempt(entry);
    }

    private CopyResult StartAttempt(Entry entry)
    {
        ISourceFile source;
        try { source = OpenSourceWithRetry(entry.SrcPath); }
        catch (FsException ex) when (ex.IsSharingViolation) { return Fail(entry, "The source is in use by another program."); }
        catch (FsException ex) when (ex.IsNotFound) { return Fail(entry, "The source no longer exists."); }
        catch (IOException ex) { return Fail(entry, ex.Message); }

        using (source)
        {
            _journal.RecordSourceVersion(entry.Id, source.Identity);
            try { _fs.CreateDirectory(entry.DestDirectory); }
            catch (IOException ex) { return Fail(entry, ex.Message); }
            catch (UnauthorizedAccessException ex) { return Fail(entry, ex.Message); }

            string tempName = TempNames.New();
            string tempPath = Path.Combine(entry.DestDirectory, tempName);
            UInt128? replaces = entry.Action == ConflictAction.Replace ? entry.SeenDest?.FileId : null;
            long attemptId = _journal.BeginAttempt(entry.Id, tempName, replaces); // intent is durable before the file exists
            _journal.ClearHashes(entry.Id); // hashes from an earlier attempt must never satisfy this one
            _journal.SetEntryState(entry.Id, EntryState.Active);

            ITempFile temp;
            try { temp = _fs.CreateTemp(tempPath); }
            catch (IOException ex)
            {
                return Fail(entry, ex.Message, attemptId);
            }

            using (temp)
            {
                UInt128 tempId = temp.GetIdentity().FileId;
                _journal.SetPhase(attemptId, AttemptPhase.TempCreated, tempFileId: tempId);
                try
                {
                    string? srcHash = WriteTemp(source, temp, entry.HashAlgorithm);
                    _journal.SetHashes(entry.Id, srcHash, null);
                    _journal.SetPhase(attemptId, AttemptPhase.Written);
                    source.Dispose(); // fully read; release it before publishing

                    if (entry.Action == ConflictAction.Replace)
                    {
                        // Never replace an existing file with one that hasn't passed verification.
                        if (srcHash is not null)
                        {
                            string tempHash = HashUnbuffered(tempPath, entry.HashAlgorithm!);
                            if (tempHash != srcHash)
                            {
                                Abandon(temp, tempPath, tempId);
                                return VerificationFailed(entry, attemptId, replaceTarget: null);
                            }
                            _journal.SetHashes(entry.Id, null, tempHash);
                            _journal.SetPhase(attemptId, AttemptPhase.VerifiedTemp);
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
                catch (IOException ex)
                {
                    Abandon(temp, tempPath, tempId);
                    return Fail(entry, ex.Message, attemptId);
                }

                // Make the rename durable. A failure here propagates; reconciliation will find the published file.
                temp.Flush();
                _journal.SetPhase(attemptId, AttemptPhase.Published, publishedFileId: tempId);
            }
        }

        return FinishPublished(_journal.GetEntry(entry.Id), _journal.GetOpenAttempt(entry.Id)!);
    }

    private CopyResult FinishPublished(Entry entry, Attempt attempt)
    {
        // The file at the destination must still be the one this attempt published, whatever phase we resume from.
        if (_fs.TryGetIdentity(entry.DestPath) is not { } current || current.FileId != attempt.PublishedFileId)
            return Fail(entry, "The destination changed after it was copied. The source was kept.", attempt.Id);

        string? verifiedHash = null;
        bool needsVerification = attempt.Phase == AttemptPhase.Published && entry.HashAlgorithm is not null && entry.DestHash is null;
        if (needsVerification)
        {
            string destHash = HashUnbuffered(entry.DestPath, entry.HashAlgorithm!);
            if (destHash != entry.SrcHash)
            {
                // The outcome and the attempt's close commit together, so a crash can't strand the entry.
                var published = _fs.TryGetIdentity(entry.DestPath);
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

    private string? WriteTemp(ISourceFile source, ITempFile temp, string? algorithm)
    {
        var hasher = algorithm is null ? null : Hashers.Create(algorithm);
        temp.Preallocate(source.Identity.Size);

        var buffer = new byte[_options.ChunkSize];
        long offset = 0;
        while (true)
        {
            int read = source.Read(buffer, offset);
            if (read == 0) break;
            var chunk = buffer.AsSpan(0, read);
            hasher?.Append(chunk);
            temp.Write(chunk, offset);
            offset += read;
        }

        temp.SetLength(offset);
        var s = source.Identity;
        temp.SetTimesAndAttributes(s.CreationTime, s.LastWriteTime, s.Attributes & CopiedAttributes);
        temp.Flush(); // data is on disk before the file can get its final name
        return hasher?.FinishHex();
    }

    private string HashUnbuffered(string path, string algorithm)
    {
        var hasher = Hashers.Create(algorithm);
        using var file = _fs.OpenForVerify(path);
        using var buffer = new AlignedBuffer(_options.ChunkSize);
        long offset = 0;
        while (true)
        {
            int read = file.Read(buffer.Span, offset);
            if (read == 0) break;
            hasher.Append(buffer.Span[..read]);
            offset += read;
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

    /// Fails the entry; when an attempt is given, it is closed in the same transaction.
    private CopyResult Fail(Entry entry, string message, long? attemptId = null)
    {
        _journal.Atomically(() =>
        {
            _journal.SetEntryState(entry.Id, EntryState.Failed, message);
            if (attemptId is { } id) _journal.SetPhase(id, AttemptPhase.Abandoned);
        });
        return new CopyResult(CopyOutcome.Failed, message);
    }
}
