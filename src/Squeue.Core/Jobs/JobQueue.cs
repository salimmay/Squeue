using Squeue.Core.Copy;
using Squeue.Core.State;

namespace Squeue.Core.Jobs;

/// What to do with files that already exist at the destination.
public enum OverwritePolicy { Replace, Skip }

/// A job as the UI sees it. DoneBytes counts finished files (done or failed) plus the current file's copied bytes.
public sealed record JobSnapshot(
    long Id,
    string Name,
    string Source,
    string DestRoot,
    JobState State,
    int TotalFiles,
    int DoneFiles,
    int FailedFiles,
    long TotalBytes,
    long DoneBytes,
    string? CurrentFile,
    double BytesPerSecond,
    string? LastError,
    DateTime CreatedAtUtc)
{
    public double Fraction => TotalBytes == 0
        ? (State == JobState.Done ? 1 : 0)
        : Math.Min(1, (double)DoneBytes / TotalBytes);
}

public interface IJobQueue : IDisposable
{
    /// Raised on the queue's own thread, never the caller's.
    event Action<JobSnapshot>? JobChanged;

    void Start();
    void Enqueue(JobPlan plan, OverwritePolicy policy, bool verify);
    void Pause(long jobId);
    void Resume(long jobId);
    void Cancel(long jobId);
}

public sealed record JobRunnerOptions
{
    public CopyOptions Copy { get; init; } = new();

    /// Minimum time between progress snapshots of a running job. State changes are always published at once.
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// How long Dispose waits for the current file to stop. The runner thread is a background thread, so the
    /// process can still exit after this; the copy protocol makes a stop mid-write safe.
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
