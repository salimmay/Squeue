using System.Collections.Concurrent;
using System.Diagnostics;
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Core.Jobs;

/// Runs queued jobs one at a time on its own thread, which is the only thread that touches the journal.
/// Commands can be called from any thread; they take effect between files, and Pause/Cancel also stop the current file.
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

    public JobRunner(IFileSystem fs, string journalPath, JobRunnerOptions? options = null)
    {
        _fs = fs;
        _journalPath = journalPath;
        _options = options ?? new JobRunnerOptions();
    }

    public event Action<JobSnapshot>? JobChanged;

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("The runner is already started.");
        _thread = new Thread(Run) { IsBackground = true, Name = "Squeue job runner" };
        _thread.Start();
    }

    public void Enqueue(JobPlan plan, OverwritePolicy policy, bool verify) =>
        _commands.Add(() => AddJob(plan, policy, verify));

    public void Pause(long jobId)
    {
        StopIfRunning(jobId);
        _commands.Add(() => ChangeState(jobId, JobState.Paused, JobState.Queued, JobState.Running));
    }

    public void Resume(long jobId) =>
        _commands.Add(() => ChangeState(jobId, JobState.Queued, JobState.Paused));

    public void Cancel(long jobId)
    {
        StopIfRunning(jobId);
        _commands.Add(() => ChangeState(jobId, JobState.Cancelled, JobState.Queued, JobState.Running, JobState.Paused));
    }

    /// Stops the current file (cleaned up, or left to finish next launch) and ends the runner thread.
    public void Dispose()
    {
        _commands.CompleteAdding();
        lock (_gate) _runningStop?.Cancel();
        _thread?.Join();
        _commands.Dispose();
    }

    private void Run()
    {
        using var journal = Journal.Open(_journalPath);
        _journal = journal;
        new Reconciler(_fs, journal).Run();
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
            var next = journal.Jobs().FirstOrDefault(j => j.State == JobState.Queued);
            if (next is not null)
            {
                RunJob(copier, next.Id);
                continue;
            }
            try { _commands.Take()(); } // nothing queued: wait for a command
            catch (InvalidOperationException) { break; } // Dispose was called while waiting
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
                if (stop.IsCancellationRequested || journal.GetJob(jobId).State != JobState.Running) break;

                currentFile = Path.GetFileName(entry.SrcPath);
                currentBytes = 0;
                var result = CopyOne(copier, entry.Id, progress, stop.Token);
                if (result.Outcome == CopyOutcome.Cancelled) break;

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
                PublishRunning(force: true);
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

    private CopyResult CopyOne(FileCopier copier, long entryId, IProgress<CopyProgress> progress, CancellationToken stop)
    {
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
        catch (IOException ex)
        {
            journal.SetEntryState(entryId, EntryState.Failed, ex.Message);
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
        while (_commands.TryTake(out var command)) command();
    }

    private JobSnapshot SnapshotOf(long jobId)
    {
        var journal = _journal!;
        var counters = JobCounters.From(journal.EntriesOf(jobId));
        return Snapshot(journal.GetJob(jobId), counters, counters.FinishedBytes, null, 0);
    }

    private static JobSnapshot Snapshot(JobRow job, JobCounters c, long doneBytes, string? currentFile, double speed) =>
        new(job.Id, job.Name ?? "Copy", job.Source ?? "", job.DestRoot ?? "", job.State,
            c.Total, c.DoneCount, c.FailedCount, c.TotalBytes, doneBytes, currentFile, speed, c.LastError,
            DateTime.FromFileTimeUtc(job.CreatedAt));

    private void Publish(JobSnapshot snapshot) => JobChanged?.Invoke(snapshot);

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
