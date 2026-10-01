using Squeue.Core.Copy;
using Squeue.Core.State;

namespace Squeue.Core.Jobs;

/// What to do with files that already exist at the destination.
public enum OverwritePolicy { Replace, Skip }

/// A job as the UI sees it. DoneBytes counts finished files (done or failed) plus the current file's copied bytes.
/// While Verifying, TotalBytes and DoneBytes describe the verification pass. WaitingFor says why a queued job hasn't started.
/// FinishedAtUtc is set once the job is done or cancelled. Checked: the job verifies its copies.
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
    DateTime CreatedAtUtc,
    string? WaitingFor = null,
    DateTime? FinishedAtUtc = null,
    bool Checked = false)
{
    public double Fraction => TotalBytes == 0
        ? (State == JobState.Done ? 1 : 0)
        : Math.Min(1, (double)DoneBytes / TotalBytes);
}

/// A file a job could not copy, and why.
public sealed record FailedFile(string Path, string Reason);

public interface IJobQueue : IDisposable
{
    /// Raised on a background thread (the coordinator's or a job's), never the caller's.
    event Action<JobSnapshot>? JobChanged;

    void Start();
    void Enqueue(JobPlan plan, OverwritePolicy policy, bool verify);
    void Pause(long jobId);
    void Resume(long jobId);
    void Cancel(long jobId);

    /// Puts the failed files of a finished job back in line and queues the job again. Does nothing if it has none.
    void RetryFailed(long jobId);

    /// Asks for the job's failed files; they arrive through FailedFilesLoaded.
    void RequestFailedFiles(long jobId);

    /// The answer to RequestFailedFiles: at most 500 files. Raised on a background thread.
    event Action<long, IReadOnlyList<FailedFile>>? FailedFilesLoaded;
}

public sealed record JobRunnerOptions
{
    public CopyOptions Copy { get; init; } = new();

    /// Minimum time between progress snapshots of a running job. State changes are always published at once.
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// How long Dispose waits for the current file to stop. The runner thread is a background thread, so the
    /// process can still exit after this; the copy protocol makes a stop mid-write safe.
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// Finished jobs older than this many days are removed from the journal at startup. 0 keeps them forever.
    public int HistoryDays { get; init; } = 90;

    /// Whether the drive holding a path is connected. Tests replace it to pretend a drive was pulled.
    internal Func<string, bool> DriveConnected { get; init; } = DriveProblems.DriveConnected;
}
