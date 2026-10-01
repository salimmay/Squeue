using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;

namespace Squeue.Tests.Jobs;

/// A JobRunner on a temp folder that records every snapshot and lets tests wait for one.
public sealed class RunnerHarness : IDisposable
{
    public static readonly JobRunnerOptions Options = new()
    {
        Copy = new CopyOptions { ChunkSize = 64 * 1024, SharingRetryTimeout = TimeSpan.Zero },
        ProgressInterval = TimeSpan.Zero,
    };

    private readonly List<JobSnapshot> _events = [];
    private readonly Dictionary<long, IReadOnlyList<FailedFile>> _failedFiles = [];
    private readonly object _lock = new();

    private readonly IDiskTopology? _topology;
    private readonly IVolumeSpace? _space;
    private readonly JobRunnerOptions _options;

    public RunnerHarness(IFileSystem? fs = null, IDiskTopology? topology = null, IVolumeSpace? space = null,
        JobRunnerOptions? options = null)
    {
        _options = options ?? Options;
        Fs = fs ?? new WindowsFileSystem();
        _topology = topology;
        _space = space;
        Runner = NewRunner();
    }

    public TestDir Dir { get; } = new();
    public IFileSystem Fs { get; }
    public JobRunner Runner { get; private set; }
    public string JournalPath => Dir.PathOf("state.db");

    /// Every snapshot so far, in the order they were published.
    public JobSnapshot[] Events
    {
        get { lock (_lock) return [.. _events]; }
    }

    /// Called on the runner's thread after each snapshot is recorded.
    public Action<JobSnapshot>? OnEvent { get; set; }

    public JobSnapshot WaitFor(Func<JobSnapshot, bool> condition, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        lock (_lock)
        {
            int seen = 0;
            while (true)
            {
                for (; seen < _events.Count; seen++)
                {
                    if (condition(_events[seen])) return _events[seen];
                }
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                    throw new TimeoutException($"The expected job event never arrived. Last event: {(_events.Count > 0 ? _events[^1] : null)}");
                Monitor.Wait(_lock, left);
            }
        }
    }

    /// The failed files reported for a job by RequestFailedFiles.
    public IReadOnlyList<FailedFile> WaitForFailedFiles(long jobId, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        lock (_lock)
        {
            while (true)
            {
                if (_failedFiles.TryGetValue(jobId, out var files)) return files;
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) throw new TimeoutException($"The failed files of job {jobId} never arrived.");
                Monitor.Wait(_lock, left);
            }
        }
    }

    /// Simulates closing and relaunching the app: disposes the runner and starts a new one on the same journal.
    /// Snapshots from before the restart are forgotten.
    public void Restart()
    {
        Runner.Dispose();
        lock (_lock) _events.Clear();
        Runner = NewRunner();
        Runner.Start();
    }

    public void Dispose()
    {
        Runner.Dispose();
        Dir.Dispose();
    }

    private JobRunner NewRunner()
    {
        var runner = new JobRunner(Fs, JournalPath, _options, _topology, _space);
        runner.JobChanged += snapshot =>
        {
            lock (_lock)
            {
                _events.Add(snapshot);
                Monitor.PulseAll(_lock);
            }
            OnEvent?.Invoke(snapshot);
        };
        runner.FailedFilesLoaded += (jobId, files) =>
        {
            lock (_lock)
            {
                _failedFiles[jobId] = files;
                Monitor.PulseAll(_lock);
            }
        };
        return runner;
    }
}
