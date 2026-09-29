using Squeue.Core.State;

namespace Squeue.Core.Jobs;

/// File and byte counts of a job, from its journal entries.
internal sealed class JobCounters
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

internal static class Snapshots
{
    public static JobSnapshot Create(JobRow job, JobState state, JobCounters c, long doneBytes, long totalBytes,
        string? currentFile, double speed, string? lastError, string? waitingFor) =>
        new(job.Id, job.Name ?? "Copy", job.Source ?? "", job.DestRoot ?? "", state,
            c.Total, c.DoneCount, c.FailedCount, totalBytes, doneBytes, currentFile, speed, lastError,
            DateTime.FromFileTimeUtc(job.CreatedAt), waitingFor);
}

/// Recognising a drive problem (as opposed to a bad file) and saying it in plain words.
internal static class DriveProblems
{
    /// Not ready, network name gone, device not connected (433, 1167), disk full (112, 39).
    public static bool IsDeviceTrouble(int code) => code is 21 or 55 or 433 or 1167 or 112 or 39;

    /// The root of the job's source or destination when that drive isn't connected.
    public static string? MissingVolume(JobRow job)
    {
        foreach (string? path in new[] { job.Source, job.DestRoot })
        {
            if (string.IsNullOrEmpty(path)) continue;
            string? root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root)) return root;
        }
        return null;
    }

    public static string NotConnected(string root) => $"{root} isn't connected. Reconnect it and resume.";

    public static string DeviceReason(JobRow job, int code) =>
        MissingVolume(job) is { } root ? NotConnected(root)
        : code is 112 or 39 ? "The destination drive is full. Free some space and resume."
        : "A drive stopped responding. Reconnect it and resume.";
}
