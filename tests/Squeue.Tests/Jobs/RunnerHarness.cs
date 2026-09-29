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
    private readonly object _lock = new();

    public RunnerHarness(IFileSystem? fs = null)
    {
        Fs = fs ?? new WindowsFileSystem();
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

    public void Dispose()
    {
        Runner.Dispose();
        Dir.Dispose();
    }

    private JobRunner NewRunner()
    {
        var runner = new JobRunner(Fs, JournalPath, Options);
        runner.JobChanged += snapshot =>
        {
            lock (_lock)
            {
                _events.Add(snapshot);
                Monitor.PulseAll(_lock);
            }
            OnEvent?.Invoke(snapshot);
        };
        return runner;
    }
}
