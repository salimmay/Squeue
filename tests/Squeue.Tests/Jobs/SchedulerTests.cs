using Squeue.Core.Jobs;

namespace Squeue.Tests.Jobs;

public class SchedulerTests
{
    private static SchedulerJob Job(long id, params string[] disks) => new(id, disks.ToHashSet());
    private static readonly Dictionary<string, long> Idle = [];
    private static string? Fits(SchedulerJob _) => null;

    [Fact]
    public void Jobs_on_different_disks_all_start()
    {
        var r = Scheduler.Pass([Job(1, "card", "hdd"), Job(2, "usb", "ssd")], Idle, Fits);
        Assert.Equal(new long[] { 1, 2 }, r.Start);
    }

    [Fact]
    public void A_job_sharing_a_disk_with_one_started_this_pass_waits()
    {
        var r = Scheduler.Pass([Job(1, "card", "hdd"), Job(2, "card", "ssd")], Idle, Fits);
        Assert.Equal(new long[] { 1 }, r.Start);
        Assert.Equal(new Wait(WaitKind.DiskInUse, 1), r.Waiting[2]);
    }

    [Fact]
    public void A_job_whose_disk_is_held_by_a_running_job_waits()
    {
        var r = Scheduler.Pass([Job(1, "card", "hdd")], new Dictionary<string, long> { ["hdd"] = 9 }, Fits);
        Assert.Empty(r.Start);
        Assert.Equal(new Wait(WaitKind.DiskInUse, 9), r.Waiting[1]);
    }

    [Fact]
    public void A_later_job_never_overtakes_an_earlier_one_on_a_shared_disk()
    {
        // Job 1 waits for the busy HDD; job 2 only needs the card and an idle SSD, but job 1 is ahead of it on the card.
        var r = Scheduler.Pass([Job(1, "card", "hdd"), Job(2, "card", "ssd")], new Dictionary<string, long> { ["hdd"] = 9 }, Fits);
        Assert.Empty(r.Start);
        Assert.Equal(new Wait(WaitKind.AheadInQueue, 1), r.Waiting[2]);
    }

    [Fact]
    public void A_job_sharing_nothing_with_a_blocked_job_starts()
    {
        var r = Scheduler.Pass([Job(1, "card", "hdd"), Job(3, "usb", "ssd")], new Dictionary<string, long> { ["hdd"] = 9 }, Fits);
        Assert.Equal(new long[] { 3 }, r.Start);
    }

    [Fact]
    public void A_card_released_by_a_verifying_job_starts_the_next_job()
    {
        // Job 9 is verifying: it holds only its destination HDD.
        var r = Scheduler.Pass([Job(2, "card", "ssd")], new Dictionary<string, long> { ["hdd"] = 9 }, Fits);
        Assert.Equal(new long[] { 2 }, r.Start);
    }

    [Fact]
    public void A_reverse_direction_job_waits_for_the_verifying_job()
    {
        var r = Scheduler.Pass([Job(2, "hdd", "card")], new Dictionary<string, long> { ["hdd"] = 9 }, Fits);
        Assert.Equal(new Wait(WaitKind.DiskInUse, 9), r.Waiting[2]);
    }

    [Fact]
    public void A_job_without_space_is_reported_and_does_not_block_later_jobs()
    {
        var r = Scheduler.Pass([Job(1, "card", "hdd"), Job(2, "card", "ssd")], Idle, j => j.Id == 1 ? "full" : null);
        Assert.Equal("full", r.NoSpace[1]);
        Assert.Equal(new long[] { 2 }, r.Start);
    }

    [Fact]
    public void The_blocker_is_the_same_whatever_the_set_order()
    {
        var busy = new Dictionary<string, long> { ["a"] = 7, ["z"] = 8 };
        Assert.Equal(7, Scheduler.Pass([Job(1, "z", "a")], busy, Fits).Waiting[1].OtherJobId);
        Assert.Equal(7, Scheduler.Pass([Job(1, "a", "z")], busy, Fits).Waiting[1].OtherJobId);
    }
}
