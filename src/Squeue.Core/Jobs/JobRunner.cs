using System.Collections.Concurrent;
using System.Diagnostics;
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Core.Jobs;

/// Runs queued jobs one at a time on its own thread, which is the only thread that touches the journal.
/// Commands can be called from any thread; they take effect between files and during a file's progress reports,
/// and Pause/Cancel also stop the current file.
public sealed class JobRunner : IJobQueue
{
    private readonly IFileSystem _fs;
    private readonly string _journalPath;
    private readonly JobRunnerOptions _options;
    private readonly BlockingCollection<Action> _commands = new();
    private readonly object _gate = new();
    private long _runningJobId;
    private CancellationTokenSource? _runningStop;
    private Thread? _thread;
    private Journal? _journal;
    private bool _disposed;
    private readonly Dictionary<long, string> _jobErrors = new(); // runner thread only

    public JobRunner(IFileSystem fs, string journalPath, JobRunnerOptions? options = null)
    {
        _fs = fs;
        _journalPath = journalPath;
        _options = options ?? new JobRunnerOptions();
    }

    public event Action<JobSnapshot>? JobChanged;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) throw new InvalidOperationException("The runner is already started.");
            _thread = new Thread(Run) { IsBackground = true, Name = "Squeue job runner" };
            _thread.Start();
        }
    }

    public void Enqueue(JobPlan plan, OverwritePolicy policy, bool verify) =>
        Post(() => AddJob(plan, policy, verify));

    public void Pause(long jobId)
    {
        StopIfRunning(jobId);
        Post(() => ChangeState(jobId, JobState.Paused, JobState.Queued, JobState.Running));
    }

    public void Resume(long jobId) =>
        Post(() => ChangeState(jobId, JobState.Queued, JobState.Paused));

    public void Cancel(long jobId)
    {
        StopIfRunning(jobId);
        Post(() => ChangeState(jobId, JobState.Cancelled, JobState.Queued, JobState.Running, JobState.Paused));
    }

    /// Stops the current file (cleaned up, or left to finish next launch) and ends the runner thread.
    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _runningStop?.Cancel();
            thread = _thread;
        }
        _commands.CompleteAdding();
        thread?.Join();
    }

    /// A command sent during or after shutdown is dropped.
    private void Post(Action command)
    {
        try { _commands.Add(command); }
        catch (InvalidOperationException) { }
    }

    private static void RunCommand(Action command)
    {
        try { command(); }
        catch (Exception ex) { Trace.TraceError($"Job runner command failed: {ex}"); }
    }

    private void Run()
    {
        try
        {
            using var journal = Journal.Open(_journalPath);
            _journal = journal;
            try { new Reconciler(_fs, journal).Run(); }
            catch (Exception ex) { Trace.TraceError($"Startup recovery failed: {ex}"); }
            foreach (var job in journal.Jobs())
            {
                // A job that was running when the app stopped goes back in the queue.
                if (job.State == JobState.Running) journal.SetJobState(job.Id, JobState.Queued);
            }
            foreach (var job in journal.Jobs()) Publish(SnapshotOf(job.Id));

            var copier = new FileCopier(_fs, journal, _options.Copy);
            while (!_commands.IsAddingCompleted)
            {
                DrainCommands();
                var next = NextRunnableJob();
                if (next is not null)
                {
                    RunJob(copier, next.Id);
                    continue;
                }
                Action command;
                try { command = _commands.Take(); } // nothing queued: wait for a command
                catch (InvalidOperationException) { break; } // Dispose was called while waiting
                RunCommand(command);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"The job runner stopped after an unexpected error: {ex}");
        }
    }

    private void RunJob(FileCopier copier, long jobId)
    {
        var journal = _journal!;
        journal.SetJobState(jobId, JobState.Running);
        var job = journal.GetJob(jobId);
        var entries = journal.EntriesOf(jobId);
        var counters = JobCounters.From(entries);
        var stop = new CancellationTokenSource();
        lock (_gate)
        {
            _runningJobId = jobId;
            _runningStop = stop;
            if (_disposed) stop.Cancel();
        }

        var clock = Stopwatch.StartNew();
        var sincePublish = Stopwatch.StartNew();
        long copiedThisRun = 0;
        long currentBytes = 0;
        string? currentFile = null;

        void PublishRunning(bool force)
        {
            if (!force && sincePublish.Elapsed < _options.ProgressInterval) return;
            sincePublish.Restart();
            double seconds = clock.Elapsed.TotalSeconds;
            double speed = seconds > 0.2 ? (copiedThisRun + currentBytes) / seconds : 0;
            Publish(Snapshot(job, counters, counters.FinishedBytes + currentBytes, currentFile, speed));
        }

        var progress = new InlineProgress(p =>
        {
            // Run waiting commands now so a new job shows up during a long file. This is the runner thread, and
            // FileCopier never reports progress inside Journal.Atomically, so the commands' journal writes are safe here.
            DrainCommands();
            // Verification re-reads the file; only the copy stage moves the bar.
            if (p.Stage == CopyStage.Copying) currentBytes = p.BytesDone;
            PublishRunning(force: false);
        });

        try
        {
            PublishRunning(force: true);
            foreach (var entry in entries)
            {
                if (entry.State is EntryState.Done or EntryState.Failed) continue;
                DrainCommands();
                if (stop.IsCancellationRequested || _commands.IsAddingCompleted || journal.GetJob(jobId).State != JobState.Running) break;

                currentFile = Path.GetFileName(entry.SrcPath);
                currentBytes = 0;
                var result = CopyOne(copier, entry.Id, progress, stop.Token, out string? stoppedReason);
                if (result.Outcome == CopyOutcome.Cancelled) break;
                if (stoppedReason is not null)
                {
                    // A drive or the journal misbehaved: this is not the file's fault. Clean up and pause the job.
                    try { new Reconciler(_fs, journal).Run(); }
                    catch (Exception ex) { Trace.TraceError($"Recovery after a failure did not complete: {ex}"); }
                    _jobErrors[jobId] = MissingVolume(job) is { } root ? NotConnected(root) : stoppedReason;
                    journal.SetJobState(jobId, JobState.Paused);
                    Publish(SnapshotOf(jobId));
                    break;
                }
                if (result.Outcome == CopyOutcome.Failed && (IsDeviceTrouble(result.Win32Error) || MissingVolume(job) is not null))
                {
                    // The drive failed, not the file: put the file back in line (its attempt is already closed) and pause.
                    journal.SetEntryState(entry.Id, EntryState.Pending);
                    _jobErrors[jobId] = DeviceReason(job, result.Win32Error);
                    journal.SetJobState(jobId, JobState.Paused);
                    Publish(SnapshotOf(jobId));
                    break;
                }

                currentBytes = 0;
                if (result.Outcome == CopyOutcome.Done)
                {
                    counters.Done(entry.PlannedSize);
                    copiedThisRun += entry.PlannedSize;
                }
                else
                {
                    counters.Failed(entry.PlannedSize, result.Message);
                }
                PublishRunning(force: false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _runningJobId = 0;
                _runningStop = null;
            }
            stop.Dispose();
        }

        DrainCommands();
        if (journal.GetJob(jobId).State == JobState.Running)
        {
            bool finished = journal.EntriesOf(jobId).All(e => e.State is EntryState.Done or EntryState.Failed);
            journal.SetJobState(jobId, finished ? JobState.Done : JobState.Queued);
        }
        Publish(SnapshotOf(jobId));
    }

    /// The first queued job whose drives are connected. Queued jobs with a missing drive are paused, not failed.
    private JobRow? NextRunnableJob()
    {
        var journal = _journal!;
        foreach (var job in journal.Jobs().Where(j => j.State == JobState.Queued))
        {
            if (MissingVolume(job) is not { } root) return job;
            _jobErrors[job.Id] = NotConnected(root);
            journal.SetJobState(job.Id, JobState.Paused);
            Publish(SnapshotOf(job.Id));
        }
        return null;
    }

    /// Not ready, network name gone, device not connected (433, 1167), disk full (112, 39).
    private static bool IsDeviceTrouble(int code) => code is 21 or 55 or 433 or 1167 or 112 or 39;

    /// The root of the job's source or destination when that drive isn't connected.
    private static string? MissingVolume(JobRow job)
    {
        foreach (string? path in new[] { job.Source, job.DestRoot })
        {
            if (string.IsNullOrEmpty(path)) continue;
            string? root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root)) return root;
        }
        return null;
    }

    private static string NotConnected(string root) => $"{root} isn't connected. Reconnect it and resume.";

    private static string DeviceReason(JobRow job, int code) =>
        MissingVolume(job) is { } root ? NotConnected(root)
        : code is 112 or 39 ? "The destination drive is full. Free some space and resume."
        : "A drive stopped responding. Reconnect it and resume.";

    private CopyResult CopyOne(FileCopier copier, long entryId, IProgress<CopyProgress> progress, CancellationToken stop, out string? stoppedReason)
    {
        stoppedReason = null;
        var journal = _journal!;
        try
        {
            var result = copier.CopyEntry(entryId, progress, stop);
            if (result.Outcome == CopyOutcome.RetryNeeded) result = copier.CopyEntry(entryId, progress, stop);

            if (result.Outcome == CopyOutcome.DestinationChanged)
            {
                // There is no per-file conflict prompt yet: keep the other file and report this one.
                const string message = "A file with this name appeared at the destination while copying. It was kept.";
                journal.SetEntryState(entryId, EntryState.Failed, message);
                return new CopyResult(CopyOutcome.Failed, message);
            }
            if (result.Outcome == CopyOutcome.RetryNeeded)
            {
                journal.SetEntryState(entryId, EntryState.Failed, result.Message);
                return new CopyResult(CopyOutcome.Failed, result.Message);
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stoppedReason = ex.Message;
            return new CopyResult(CopyOutcome.Failed, ex.Message);
        }
    }

    private void AddJob(JobPlan plan, OverwritePolicy policy, bool verify)
    {
        var journal = _journal!;
        long jobId = 0;
        journal.Atomically(() =>
        {
            jobId = journal.CreateJob(verify ? "xxh3" : null, plan.Name, plan.Source, plan.DestRoot);
            foreach (var file in plan.Files)
            {
                if (file.ExistingDest is not null && policy == OverwritePolicy.Skip) continue;
                var action = file.ExistingDest is null ? ConflictAction.Create : ConflictAction.Replace;
                journal.AddEntry(jobId, file.SourcePath, file.DestPath, file.Size, action, file.ExistingDest);
            }
        });
        Publish(SnapshotOf(jobId));
    }

    private void ChangeState(long jobId, JobState to, params JobState[] from)
    {
        var journal = _journal!;
        if (!from.Contains(journal.GetJob(jobId).State)) return;
        journal.SetJobState(jobId, to);
        if (to == JobState.Queued) _jobErrors.Remove(jobId);
        Publish(SnapshotOf(jobId));
    }

    private void StopIfRunning(long jobId)
    {
        lock (_gate)
        {
            if (_runningJobId == jobId) _runningStop?.Cancel();
        }
    }

    private void DrainCommands()
    {
        while (_commands.TryTake(out var command)) RunCommand(command);
    }

    private JobSnapshot SnapshotOf(long jobId)
    {
        var journal = _journal!;
        var counters = JobCounters.From(journal.EntriesOf(jobId));
        return Snapshot(journal.GetJob(jobId), counters, counters.FinishedBytes, null, 0);
    }

    private JobSnapshot Snapshot(JobRow job, JobCounters c, long doneBytes, string? currentFile, double speed) =>
        new(job.Id, job.Name ?? "Copy", job.Source ?? "", job.DestRoot ?? "", job.State,
            c.Total, c.DoneCount, c.FailedCount, c.TotalBytes, doneBytes, currentFile, speed, _jobErrors.TryGetValue(job.Id, out var e) ? e : c.LastError,
            DateTime.FromFileTimeUtc(job.CreatedAt));

    private void Publish(JobSnapshot snapshot)
    {
        foreach (var handler in JobChanged?.GetInvocationList() ?? [])
        {
            try { ((Action<JobSnapshot>)handler)(snapshot); }
            catch (Exception ex) { Trace.TraceError($"A job event subscriber failed: {ex}"); }
        }
    }

    private sealed class InlineProgress(Action<CopyProgress> report) : IProgress<CopyProgress>
    {
        public void Report(CopyProgress value) => report(value);
    }

    private sealed class JobCounters
    {
        public int Total { get; private set; }
        public int DoneCount { get; private set; }
        public int FailedCount { get; private set; }
        public long TotalBytes { get; private set; }
        public long FinishedBytes { get; private set; }
        public string? LastError { get; private set; }

        public static JobCounters From(IReadOnlyList<EntryRow> entries)
        {
            var c = new JobCounters();
            foreach (var e in entries)
            {
                c.Total++;
                c.TotalBytes += e.PlannedSize;
                if (e.State == EntryState.Done) c.Done(e.PlannedSize);
                else if (e.State == EntryState.Failed) c.Failed(e.PlannedSize, e.Error);
            }
            return c;
        }

        public void Done(long bytes)
        {
            DoneCount++;
            FinishedBytes += bytes;
        }

        public void Failed(long bytes, string? error)
        {
            FailedCount++;
            FinishedBytes += bytes;
            LastError = error;
        }
    }
}
