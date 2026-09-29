namespace Squeue.Core.Copy;

public sealed record CopyOptions
{
    /// Bytes per read/write. Must be a positive multiple of 4096.
    public int ChunkSize { get; init; } = 4 * 1024 * 1024;

    /// How long to keep retrying when another program has the source open for writing.
    public TimeSpan SharingRetryTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan SharingRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}

public enum CopyOutcome
{
    Done,
    Failed,

    /// The destination isn't what the conflict decision was made against. Nothing was replaced.
    DestinationChanged,

    /// Verification failed once; the entry is pending and will be copied again.
    RetryNeeded,
}

public sealed record CopyResult(CopyOutcome Outcome, string? Message = null);
