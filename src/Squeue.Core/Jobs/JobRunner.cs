using System.Collections.Concurrent;
using System.Diagnostics;
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Core.Jobs;

/// The coordinator. One coordinator thread owns the queue, the disks each running job holds and every scheduling
/// decision; each running job has its own worker thread and journal connection. A job starts only when all the
/// disks its next phase needs are free, so jobs on different disks run in parallel and jobs sharing a disk run in
/// queue order. A copy job moving on to verification frees its source disks. Commands can be called from any
/// thread; they take effect on the coordinator thread, and a running job's disks are released only after its
/// worker has stopped.
public sealed class JobRunner : IJobQueue
{
    private readonly IFileSystem _fs;
    private readonly string _journalPath;
    private readonly JobRunnerOptions _options;
    private readonly IDiskTopology _topology;
    private readonly IVolumeSpace _space;
    private readonly BlockingCollection<Action> _commands = new();
    private readonly object _gate = new();
    private readonly Dictionary<long, CancellationTokenSource> _stops = []; // under _gate
    private Thread? _thread;
    private bool _disposed;

    // Coordinator thread only.
    private Journal? _journal;
    private readonly Dictionary<long, Worker> _workers = [];
    private readonly Dictionary<long, string> _jobErrors = [];
    private readonly Dictionary<long, string> _waiting = [];
    private long _nextRunId;

    public JobRunner(IFileSystem fs, string journalPath, JobRunnerOptions? options = null,
        IDiskTopology? topology = null, IVolumeSpace? space = null)
    {
        _fs = fs;
        _journalPath = journalPath;
        _options = options ?? new JobRunnerOptions();
        _topology = topology ?? new WindowsDiskTopology();
        _space = space ?? new WindowsVolumeSpace();
    }

    public event Action<JobSnapshot>? JobChanged;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) throw new InvalidOperationException("The runner is already started.");
            _thread = new Thread(Run) { IsBackground = true, Name = "Squeue coordinator" };
            _thread.Start();
        }
    }

    public void Enqueue(JobPlan plan, OverwritePolicy policy, bool verify) =>
        Post(() => AddJob(plan, policy, verify));

    // Pause and Cancel post the state change before stopping the worker, so it is always handled before the worker's
    // exit report: otherwise the exit could re-queue the job and a pass restart it before the pause arrives.
    // Only the run current when the command was given is stopped, never one started after it.
    public void Pause(long jobId)
    {
        var run = CurrentRun(jobId);
        Post(() => ChangeState(jobId, JobState.Paused, JobState.Queued, JobState.Running, JobState.Verifying));
        StopIfRunning(jobId, run);
    }

    public void Resume(long jobId) =>
        Post(() => ChangeState(jobId, JobState.Queued, JobState.Paused));

    public void Cancel(long jobId)
    {
        var run = CurrentRun(jobId);
        Post(() => ChangeState(jobId, JobState.Cancelled, JobState.Queued, JobState.Running, JobState.Verifying, JobState.Paused));
        StopIfRunning(jobId, run);
    }

    /// Stops every running job (its current file is cleaned up or left to finish next launch) and ends the
    /// coordinator, waiting at most ShutdownTimeout.
    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var stop in _stops.Values) stop.Cancel();
            thread = _thread;
        }
        _commands.CompleteAdding();
        if (thread is not null && !thread.Join(_options.ShutdownTimeout))
            Trace.TraceWarning("The job coordinator did not stop in time; it is left to end with the process.");
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
        catch (Exception ex) { Trace.TraceError($"Job coordinator command failed: {ex}"); }
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
                // A job that was running or verifying when the app stopped goes back in the queue.
                if (job.State is JobState.Running or JobState.Verifying) journal.SetJobState(job.Id, JobState.Queued);
            }
            // Unfinished jobs, and today's finished ones; older history isn't shown, so launch time stays flat.
            var today = DateTime.Now.Date;
            foreach (var job in journal.Jobs())
            {
                bool finished = job.State is JobState.Done or JobState.Cancelled;
                if (!finished || DateTime.FromFileTimeUtc(job.CreatedAt).ToLocalTime().Date == today) Publish(SnapshotOf(job.Id));
            }

            SchedulePassSafely();
            while (true)
            {
                Action command;
                try { command = _commands.Take(); }
                catch (InvalidOperationException) { break; } // Dispose was called
                RunCommand(command);
                while (_commands.TryTake(out var more)) RunCommand(more);
                SchedulePassSafely();
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"The job coordinator stopped after an unexpected error: {ex}");
        }
        finally
        {
            StopWorkers();
        }
    }

    private void SchedulePassSafely()
    {
        try { SchedulePass(); }
        catch (Exception ex) { Trace.TraceError($"Scheduling failed: {ex}"); }
    }

    private void SchedulePass()
    {
        if (_commands.IsAddingCompleted) return;
        var journal = _journal!;
        var queue = new List<SchedulerJob>();
        var resources = new Dictionary<long, JobResources>();
        var disksOf = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in journal.Jobs())
        {
            if (job.State != JobState.Queued || _workers.ContainsKey(job.Id)) continue;
            JobResources r;
            string? missing;
            try
            {
                r = ResourcesOf(job, journal.EntriesOf(job.Id), disksOf);
                // A job with only copies left to verify needs just its destination; the card may be gone by now.
                missing = r.MissingRoot
                    ?? (r.VerifyOnly
                        ? DriveProblems.MissingDestination(job, _options.DriveConnected)
                        : DriveProblems.MissingVolume(job, _options.DriveConnected));
            }
            catch (Exception ex)
            {
                // One job with a bad path (or a busy journal) must not stop the others from being scheduled.
                Trace.TraceError($"Couldn't work out the drives of job {job.Id}: {ex}");
                PauseSafely(job.Id, CouldNotRead(ex));
                continue;
            }
            if (missing is not null)
            {
                PauseSafely(job.Id, DriveProblems.NotConnected(missing));
                continue;
            }
            queue.Add(new SchedulerJob(job.Id, r.Disks));
            resources[job.Id] = r;
        }

        var busy = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var worker in _workers.Values)
        {
            foreach (string disk in worker.Disks) busy[disk] = worker.JobId;
        }

        var result = Scheduler.Pass(queue, busy, job =>
        {
            try { return CapacityProblem(resources[job.Id]); }
            catch (Exception ex)
            {
                Trace.TraceError($"Couldn't check the free space for job {job.Id}: {ex}");
                return CouldNotRead(ex);
            }
        });
        foreach (var (jobId, problem) in result.NoSpace) PauseSafely(jobId, problem);
        foreach (long jobId in result.Start) StartWorker(jobId, resources[jobId]);
        foreach (var (jobId, wait) in result.Waiting)
        {
            string text = WaitingText(wait);
            if (_waiting.TryGetValue(jobId, out var old) && old == text) continue;
            _waiting[jobId] = text;
            Publish(SnapshotOf(jobId));
        }
    }

    private sealed record JobResources(
        IReadOnlySet<string> Disks, IReadOnlySet<string> DestDisks, string? MissingRoot,
        string DestRoot, IReadOnlyList<long> PendingSizes, bool VerifyOnly);

    /// The disks the job's next phase needs: the destination's, plus the source's while files remain to copy.
    /// A job with only published copies left to verify needs just the destination.
    private JobResources ResourcesOf(JobRow job, IReadOnlyList<EntryRow> entries, Dictionary<string, IReadOnlyList<string>> disksOf)
    {
        string dest = job.DestRoot ?? "";
        var pending = entries.Where(e => e.State == EntryState.Pending).ToList();
        bool verifyOnly = pending.Count == 0 && entries.Any(e => e.State == EntryState.Active);

        IReadOnlyList<string> Resolve(string path)
        {
            if (!disksOf.TryGetValue(path, out var disks)) disksOf[path] = disks = _topology.DisksOf(path);
            return disks;
        }

        var destDisks = new HashSet<string>(StringComparer.Ordinal);
        if (dest.Length > 0)
        {
            var d = Resolve(dest);
            if (d.Count == 0) return Missing(Path.GetPathRoot(dest) ?? dest);
            destDisks.UnionWith(d);
        }
        var all = new HashSet<string>(destDisks, StringComparer.Ordinal);
        foreach (string folder in pending.Select(e => Path.GetDirectoryName(e.SrcPath) ?? e.SrcPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var d = Resolve(folder);
            if (d.Count == 0) return Missing(Path.GetPathRoot(folder) ?? folder);
            all.UnionWith(d);
        }
        return new JobResources(all, destDisks, null, dest, pending.Select(e => e.PlannedSize).ToList(), verifyOnly);

        JobResources Missing(string root) => new(new HashSet<string>(), new HashSet<string>(), root, dest, [], false);
    }

    private string? CapacityProblem(JobResources r)
    {
        if (r.PendingSizes.Count == 0 || r.DestRoot.Length == 0) return null;
        if (_space.Query(r.DestRoot) is not { } space) return DriveProblems.NotConnected(Path.GetPathRoot(r.DestRoot) ?? r.DestRoot);
        long need = Capacity.Need(r.PendingSizes, space.ClusterSize);
        long reserved = 0;
        foreach (var worker in _workers.Values)
        {
            // Space a running job has already allocated is gone from free space; count only what it still has to write.
            if (!string.Equals(worker.DestVolume, space.Root, StringComparison.OrdinalIgnoreCase)) continue;
            reserved += Capacity.Need(
                _journal!.EntriesOf(worker.JobId).Where(e => e.State == EntryState.Pending).Select(e => e.PlannedSize),
                space.ClusterSize);
        }
        return Capacity.Problem(space, need, reserved);
    }

    private string WaitingText(Wait wait)
    {
        string other = _journal!.GetJob(wait.OtherJobId).Name ?? "another job";
        return wait.Kind == WaitKind.DiskInUse
            ? $"“{other}” is using the same drive"
            : $"“{other}” is ahead of it on the same drive";
    }

    private sealed class Worker(long jobId, long runId, IReadOnlySet<string> disks, IReadOnlySet<string> destDisks,
        CancellationTokenSource stop, string? destVolume)
    {
        public long JobId { get; } = jobId;
        public long RunId { get; } = runId;
        public IReadOnlySet<string> Disks { get; set; } = disks;
        public IReadOnlySet<string> DestDisks { get; } = destDisks;
        public CancellationTokenSource Stop { get; } = stop;
        public string? DestVolume { get; } = destVolume;
        public Thread? Thread { get; set; }
    }

    private void StartWorker(long jobId, JobResources r)
    {
        var journal = _journal!;
        long runId = ++_nextRunId;
        var stop = new CancellationTokenSource();
        lock (_gate)
        {
            if (_disposed) stop.Cancel();
            _stops[jobId] = stop;
        }
        string? destVolume = r.DestRoot.Length > 0 ? _space.Query(r.DestRoot)?.Root : null;
        var worker = new Worker(jobId, runId, r.Disks, r.DestDisks, stop, destVolume);
        _workers[jobId] = worker;
        _jobErrors.Remove(jobId);
        _waiting.Remove(jobId);
        journal.SetJobState(jobId, r.VerifyOnly ? JobState.Verifying : JobState.Running);
        Publish(SnapshotOf(jobId));
        worker.Thread = new Thread(() => RunWorker(jobId, runId, stop.Token)) { IsBackground = true, Name = $"Squeue job {jobId}" };
        worker.Thread.Start();
    }

    /// Worker thread.
    private void RunWorker(long jobId, long runId, CancellationToken stop)
    {
        string? pauseReason;
        try
        {
            using var journal = Journal.Open(_journalPath);
            pauseReason = new JobWorker(_fs, journal, _options, jobId, Publish,
                () => Post(() => OnVerifying(jobId, runId)), stop).Run();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Job {jobId} stopped after an unexpected error: {ex}");
            pauseReason = stop.IsCancellationRequested ? null : ex.Message;
        }
        Post(() => OnWorkerExited(jobId, runId, pauseReason));
    }

    /// The worker finished copying and is verifying: only the destination's disks stay held.
    private void OnVerifying(long jobId, long runId)
    {
        if (!_workers.TryGetValue(jobId, out var worker) || worker.RunId != runId) return;
        worker.Disks = worker.DestDisks;
        if (_journal!.GetJob(jobId).State == JobState.Running) _journal.SetJobState(jobId, JobState.Verifying);
    }

    /// The worker has stopped: release its disks and settle the job's state.
    private void OnWorkerExited(long jobId, long runId, string? pauseReason)
    {
        if (!_workers.TryGetValue(jobId, out var worker) || worker.RunId != runId) return;
        _workers.Remove(jobId);
        lock (_gate)
        {
            if (_stops.TryGetValue(jobId, out var stop) && stop == worker.Stop) _stops.Remove(jobId);
        }
        worker.Stop.Dispose();

        var journal = _journal!;
        if (journal.GetJob(jobId).State is JobState.Running or JobState.Verifying)
        {
            if (pauseReason is not null)
            {
                _jobErrors[jobId] = pauseReason;
                journal.SetJobState(jobId, JobState.Paused);
            }
            else
            {
                // Files whose verification failed are Pending again: the job re-queues for a retry pass.
                bool finished = journal.EntriesOf(jobId).All(e => e.State is EntryState.Done or EntryState.Failed);
                journal.SetJobState(jobId, finished ? JobState.Done : JobState.Queued);
            }
        }
        Publish(SnapshotOf(jobId));
    }

    private void StopWorkers()
    {
        var clock = Stopwatch.StartNew();
        foreach (var worker in _workers.Values)
        {
            worker.Stop.Cancel();
            var left = _options.ShutdownTimeout - clock.Elapsed;
            if (worker.Thread is not null && (left <= TimeSpan.Zero || !worker.Thread.Join(left)))
                Trace.TraceWarning($"Job {worker.JobId} did not stop in time; it is left to end with the process.");
        }
    }

    private void ChangeState(long jobId, JobState to, params JobState[] from)
    {
        var journal = _journal!;
        if (!from.Contains(journal.GetJob(jobId).State)) return;
        journal.SetJobState(jobId, to);
        if (to == JobState.Queued) _jobErrors.Remove(jobId);
        _waiting.Remove(jobId);
        if (_workers.TryGetValue(jobId, out var worker))
        {
            // Also catches a worker started after the caller's StopIfRunning. Its new state is reported once it has stopped.
            if (to is JobState.Paused or JobState.Cancelled) worker.Stop.Cancel();
            return;
        }
        Publish(SnapshotOf(jobId));
    }

    private static string CouldNotRead(Exception ex) => $"Squeue couldn't read this job's drives: {ex.Message}";

    /// Pausing one job failing (a busy journal) must not end the pass for the others.
    private void PauseSafely(long jobId, string reason)
    {
        try { PauseWithReason(jobId, reason); }
        catch (Exception ex) { Trace.TraceError($"Couldn't pause job {jobId}: {ex}"); }
    }

    private void PauseWithReason(long jobId, string reason)
    {
        _jobErrors[jobId] = reason;
        _waiting.Remove(jobId);
        _journal!.SetJobState(jobId, JobState.Paused);
        Publish(SnapshotOf(jobId));
    }

    private CancellationTokenSource? CurrentRun(long jobId)
    {
        lock (_gate) return _stops.GetValueOrDefault(jobId);
    }

    /// A run leaves _stops (under the lock) before its token source is disposed, so a match is never disposed.
    private void StopIfRunning(long jobId, CancellationTokenSource? run)
    {
        if (run is null) return;
        lock (_gate)
        {
            if (_stops.TryGetValue(jobId, out var stop) && stop == run) stop.Cancel();
        }
    }

    private JobSnapshot SnapshotOf(long jobId)
    {
        var journal = _journal!;
        var job = journal.GetJob(jobId);
        var c = JobCounters.From(journal.EntriesOf(jobId));
        return Snapshots.Create(job, job.State, c, c.FinishedBytes, c.TotalBytes, null, 0,
            _jobErrors.TryGetValue(jobId, out var e) ? e : c.LastError, _waiting.GetValueOrDefault(jobId));
    }

    private void AddJob(JobPlan plan, OverwritePolicy policy, bool verify)
    {
        var journal = _journal!;
        long jobId = 0;
        // An existing file is only ever replaced by a verified copy, whatever the caller asked for.
        bool mustVerify = verify || (policy == OverwritePolicy.Replace && plan.ExistingCount > 0);
        journal.Atomically(() =>
        {
            jobId = journal.CreateJob(mustVerify ? "xxh3" : null, plan.Name, plan.Source, plan.DestRoot);
            foreach (var file in plan.Files)
            {
                if (file.ExistingDest is not null && policy == OverwritePolicy.Skip)
                {
                    if (file.ExistingLooksSame) continue;
                    // Same name, different file: keep it, and report this one instead of silently leaving it out.
                    long entryId = journal.AddEntry(jobId, file.SourcePath, file.DestPath, file.Size, ConflictAction.Create, file.ExistingDest);
                    journal.SetEntryState(entryId, EntryState.Failed, DifferentFileKept);
                    continue;
                }
                var action = file.ExistingDest is null ? ConflictAction.Create : ConflictAction.Replace;
                journal.AddEntry(jobId, file.SourcePath, file.DestPath, file.Size, action, file.ExistingDest);
            }
        });
        Publish(SnapshotOf(jobId));
    }

    private const string DifferentFileKept = "A different file with this name is already there. It was not replaced.";

    private void Publish(JobSnapshot snapshot)
    {
        foreach (var handler in JobChanged?.GetInvocationList() ?? [])
        {
            try { ((Action<JobSnapshot>)handler)(snapshot); }
            catch (Exception ex) { Trace.TraceError($"A job event subscriber failed: {ex}"); }
        }
    }
}
