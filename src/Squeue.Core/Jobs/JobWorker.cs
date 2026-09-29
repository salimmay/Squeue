using System.Diagnostics;
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Core.Jobs;

/// Runs one job on the calling (worker) thread with its own journal connection: first it copies every file that
/// still needs copying (publishing each one, verification deferred), then it verifies every published copy.
/// It never touches the coordinator's state. It publishes progress, calls enteringVerification when it moves on
/// to verifying (the coordinator then frees the source drives), and returns why it paused, or null.
internal sealed class JobWorker
{
    private readonly IFileSystem _fs;
    private readonly Journal _journal;
    private readonly JobRunnerOptions _options;
    private readonly long _jobId;
    private readonly Action<JobSnapshot> _publish;
    private readonly Action _enteringVerification;
    private readonly CancellationToken _stop;
    private readonly Stopwatch _sincePublish = Stopwatch.StartNew();
    private readonly Stopwatch _clock = new();
    private JobRow _job = null!;
    private JobCounters _counters = null!;
    private JobState _state = JobState.Running;
    private long _barDone, _barTotal, _current, _movedThisStage;
    private string? _currentFile;

    public JobWorker(IFileSystem fs, Journal journal, JobRunnerOptions options, long jobId,
        Action<JobSnapshot> publish, Action enteringVerification, CancellationToken stop)
    {
        _fs = fs;
        _journal = journal;
        _options = options;
        _jobId = jobId;
        _publish = publish;
        _enteringVerification = enteringVerification;
        _stop = stop;
    }

    public string? Run()
    {
        // Attempts this job left open (for example when its drive was missing at startup) are settled first.
        Reconcile();
        _job = _journal.GetJob(_jobId);
        var copier = new FileCopier(_fs, _journal, _options.Copy);
        bool defer = _job.HashAlgorithm is not null;
        var entries = _journal.EntriesOf(_jobId);
        _counters = JobCounters.From(entries);

        // Copy phase. A drive failing a file ends it, but what is already published is still verified before pausing.
        string? pauseAfterVerifying = null;
        long published = entries.Where(e => e.State == EntryState.Active).Sum(e => e.PlannedSize);
        var toCopy = entries.Where(e => e.State == EntryState.Pending).ToList();
        if (toCopy.Count > 0)
        {
            StartStage(JobState.Running, _counters.TotalBytes, _counters.FinishedBytes + published);
            foreach (var entry in toCopy)
            {
                if (_stop.IsCancellationRequested) return null;
                var result = CopyOne(copier, entry, defer, allowRetry: true, out string? stopped);
                if (Stops(entry, result, stopped, JobState.Running) is { } outcome)
                {
                    if (!outcome.VerifyFirst) return outcome.Reason;
                    pauseAfterVerifying = outcome.Reason;
                    break;
                }
                Count(entry, result);
            }
        }
        if (_stop.IsCancellationRequested) return null;

        // Verification phase: read back every published copy. It needs only the destination drive.
        var toVerify = _journal.EntriesOf(_jobId).Where(e => e.State == EntryState.Active).ToList();
        if (toVerify.Count == 0) return pauseAfterVerifying;
        _enteringVerification();
        StartStage(JobState.Verifying, toVerify.Sum(e => e.PlannedSize), 0);
        foreach (var entry in toVerify)
        {
            if (_stop.IsCancellationRequested) return null;
            // A failed verification leaves the file Pending; the job's next pass re-copies it with the source drive.
            var result = CopyOne(copier, entry, deferVerification: false, allowRetry: false, out string? stopped);
            if (Stops(entry, result, stopped, JobState.Verifying) is { } outcome) return outcome.Reason;
            Count(entry, result);
        }
        return pauseAfterVerifying;
    }

    /// VerifyFirst: the drive failed one file, not the job's state, so copies already published can still be verified.
    private sealed record Stop(string? Reason, bool VerifyFirst = false);

    /// Whether this result ends the run: a stop request (null reason), or a drive/journal problem (a reason).
    /// The stage decides which drives count: copying needs source and destination, verifying only the destination
    /// (the source may be gone by then, legitimately: the coordinator freed it).
    private Stop? Stops(EntryRow entry, CopyResult result, string? stopped, JobState stage)
    {
        if (result.Outcome == CopyOutcome.Cancelled) return new Stop(null);
        if (stopped is not null)
        {
            // A drive or the journal misbehaved: not the file's fault. Settle this job's attempts and pause.
            Reconcile();
            return new Stop(MissingRoot(stage) is { } root ? DriveProblems.NotConnected(root) : stopped);
        }
        if (result.Outcome != CopyOutcome.Failed) return null;
        string? missing = MissingRoot(stage);
        // Anything else is the file's own failure: it stays Failed with its message and the job goes on.
        if (!DriveProblems.IsDeviceTrouble(result.Win32Error) && missing is null) return null;
        // The drive failed, not the file: put the file back in line (its attempt is already closed) and pause.
        _journal.SetEntryState(entry.Id, EntryState.Pending);
        // While copying, what is already published is verified first: verifying reads only the destination, and if
        // that is the drive that failed, verification pauses on its own.
        return new Stop(DriveProblems.DeviceReason(missing, result.Win32Error), VerifyFirst: stage == JobState.Running);
    }

    /// The root of a drive the stage needs that isn't connected.
    private string? MissingRoot(JobState stage) => stage == JobState.Verifying
        ? DriveProblems.MissingDestination(_job, _options.DriveConnected)
        : DriveProblems.MissingVolume(_job, _options.DriveConnected);

    private void Count(EntryRow entry, CopyResult result)
    {
        if (result.Outcome == CopyOutcome.Done) _counters.Done(entry.PlannedSize);
        else if (result.Outcome == CopyOutcome.Failed) _counters.Failed(entry.PlannedSize, result.Message);
        _barDone += entry.PlannedSize;
        _movedThisStage += entry.PlannedSize;
        _current = 0;
        Publish(force: false);
    }

    private CopyResult CopyOne(FileCopier copier, EntryRow entry, bool deferVerification, bool allowRetry, out string? stopped)
    {
        stopped = null;
        _currentFile = Path.GetFileName(entry.SrcPath);
        _current = 0;
        var progress = new InlineProgress(OnProgress);
        try
        {
            var result = copier.CopyEntry(entry.Id, progress, _stop, deferVerification);
            if (result.Outcome == CopyOutcome.RetryNeeded && allowRetry)
                result = copier.CopyEntry(entry.Id, progress, _stop, deferVerification);

            if (result.Outcome == CopyOutcome.DestinationChanged)
            {
                // There is no per-file conflict prompt yet: keep the other file and report this one.
                const string message = "A file with this name appeared at the destination while copying. It was kept.";
                _journal.SetEntryState(entry.Id, EntryState.Failed, message);
                return new CopyResult(CopyOutcome.Failed, message);
            }
            if (result.Outcome == CopyOutcome.RetryNeeded && allowRetry)
            {
                _journal.SetEntryState(entry.Id, EntryState.Failed, result.Message);
                return new CopyResult(CopyOutcome.Failed, result.Message);
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopped = ex.Message;
            return new CopyResult(CopyOutcome.Failed, ex.Message);
        }
    }

    private void Reconcile()
    {
        try { new Reconciler(_fs, _journal).Run(entry => entry.JobId == _jobId); }
        catch (Exception ex) { Trace.TraceError($"Recovery for job {_jobId} did not complete: {ex}"); }
    }

    private void StartStage(JobState state, long total, long done)
    {
        _state = state;
        _barTotal = total;
        _barDone = done;
        _current = 0;
        _movedThisStage = 0;
        _currentFile = null;
        _clock.Restart();
        Publish(force: true);
    }

    private void OnProgress(CopyProgress p)
    {
        // Each stage's bar follows its own pass: the copy stage moves it while copying, verification while verifying.
        bool counts = _state == JobState.Verifying ? p.Stage == CopyStage.Verifying : p.Stage == CopyStage.Copying;
        if (counts) _current = p.BytesDone;
        Publish(force: false);
    }

    private void Publish(bool force)
    {
        // Once asked to stop, the coordinator reports the job's state after this worker has stopped.
        if (_stop.IsCancellationRequested) return;
        if (!force && _sincePublish.Elapsed < _options.ProgressInterval) return;
        _sincePublish.Restart();
        double seconds = _clock.Elapsed.TotalSeconds;
        double speed = seconds > 0.2 ? (_movedThisStage + _current) / seconds : 0;
        _publish(Snapshots.Create(_job, _state, _counters, _barDone + _current, _barTotal, _currentFile, speed,
            _counters.LastError, null));
    }

    private sealed class InlineProgress(Action<CopyProgress> report) : IProgress<CopyProgress>
    {
        public void Report(CopyProgress value) => report(value);
    }
}
