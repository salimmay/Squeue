using Squeue.Core.FileSystem;

namespace Squeue.Core.State;

public enum ConflictAction { Create, Replace }

public enum EntryState { Pending, Active, Done, Failed }

/// Journal phases of one attempt, in protocol order. Finished and Abandoned are terminal.
public enum AttemptPhase { Intent, TempCreated, Written, VerifiedTemp, Published, Verified, Finished, Abandoned }

public enum JobState { Queued, Running, Paused, Done, Cancelled }

public sealed record JobRow(
    long Id,
    string? Name,
    string? Source,
    string? DestRoot,
    JobState State,
    string? HashAlgorithm,
    long CreatedAt);

public sealed record EntryRow(long Id, string SrcPath, long PlannedSize, EntryState State, string? Error);

public sealed record Entry(
    long Id,
    long JobId,
    string SrcPath,
    string DestPath,
    long PlannedSize,
    ConflictAction Action,
    FileIdentity? SeenDest,
    EntryState State,
    string? HashAlgorithm,
    FileIdentity? SourceVersion,
    string? SrcHash,
    string? DestHash,
    int VerifyFailures,
    string? Error)
{
    public string DestDirectory => Path.GetDirectoryName(DestPath)!;
}

public sealed record Attempt(
    long Id,
    long EntryId,
    AttemptPhase Phase,
    string TempName,
    UInt128? TempFileId,
    UInt128? ReplacedFileId,
    UInt128? PublishedFileId,
    long StartedAt);
