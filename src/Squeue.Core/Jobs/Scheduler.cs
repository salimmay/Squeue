namespace Squeue.Core.Jobs;

/// A queued job as the scheduler sees it: the disks its next phase needs.
public sealed record SchedulerJob(long Id, IReadOnlySet<string> Disks);

public enum WaitKind
{
    /// A running job (or one started earlier in this pass) holds one of its disks.
    DiskInUse,

    /// An earlier queued job that is itself waiting shares one of its disks.
    AheadInQueue,
}

public sealed record Wait(WaitKind Kind, long OtherJobId);

public sealed record SchedulerResult(
    IReadOnlyList<long> Start,
    IReadOnlyDictionary<long, Wait> Waiting,
    IReadOnlyDictionary<long, string> NoSpace);

/// One scheduling pass. A pure function, so its rules are table-tested:
/// a job starts only when all its disks are free; a job never starts ahead of an earlier queued job that shares
/// any of its disks; jobs that share no disk with a blocked job start as soon as their own disks are free.
public static class Scheduler
{
    public static SchedulerResult Pass(IReadOnlyList<SchedulerJob> queue, IReadOnlyDictionary<string, long> busy,
        Func<SchedulerJob, string?> capacityProblem)
    {
        var holders = new Dictionary<string, (long Job, bool Holds)>(StringComparer.Ordinal);
        foreach (var (disk, job) in busy) holders[disk] = (job, true);
        var start = new List<long>();
        var waiting = new Dictionary<long, Wait>();
        var noSpace = new Dictionary<long, string>();

        foreach (var job in queue)
        {
            var disks = job.Disks.Order(StringComparer.Ordinal).ToList();
            (long Job, bool Holds)? blocker = null;
            foreach (string disk in disks)
            {
                if (holders.TryGetValue(disk, out var h)) { blocker = h; break; }
            }

            if (blocker is { } b)
            {
                waiting[job.Id] = new Wait(b.Holds ? WaitKind.DiskInUse : WaitKind.AheadInQueue, b.Job);
                // Its disks now belong to it for the rest of this pass, so no later job overtakes it on them.
                foreach (string disk in disks) holders.TryAdd(disk, (job.Id, false));
                continue;
            }

            // A job that can't fit is paused by the caller; it leaves the queue, so it blocks nothing.
            if (capacityProblem(job) is { } problem)
            {
                noSpace[job.Id] = problem;
                continue;
            }

            foreach (string disk in disks) holders[disk] = (job.Id, true);
            start.Add(job.Id);
        }

        return new SchedulerResult(start, waiting, noSpace);
    }
}
