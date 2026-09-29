using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;
using Squeue.Core.State;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Jobs;

public class MultiDriveTests
{
    // Folder name → pretend disk. cardA1 and cardA2 are two folders on the same card.
    private static FolderTopology Topology() => new(
        ("cardA1", "card-a"), ("cardA2", "card-a"), ("cardB", "card-b"),
        ("hddA", "hdd-a"), ("hddB", "hdd-b"), ("ssd", "ssd"));

    private static JobPlan Plan(RunnerHarness h, string source, string dest)
    {
        h.Dir.Write(Path.Combine(source, $"{source}.bin"), TestDir.RandomBytes(20_000, source.Length));
        return JobPlanner.Plan(h.Fs, [h.Dir.PathOf(source)], h.Dir.PathOf(dest));
    }

    [Fact]
    public void Jobs_on_different_drives_run_at_the_same_time()
    {
        var gates = new GateFileSystem(new WindowsFileSystem());
        using var h = new RunnerHarness(gates, Topology());
        var a = Plan(h, "cardA1", "hddA");
        var b = Plan(h, "cardB", "hddB");
        var holdA = gates.HoldSourcesUnder(h.Dir.PathOf("cardA1"));
        try
        {
            h.Runner.Start();
            h.Runner.Enqueue(a, OverwritePolicy.Replace, verify: true);
            var first = h.WaitFor(s => s.Name == "cardA1" && s.State == JobState.Running);
            h.Runner.Enqueue(b, OverwritePolicy.Replace, verify: true);

            h.WaitFor(s => s.Name == "cardB" && s.State == JobState.Done);

            Assert.DoesNotContain(h.Events, s => s.Id == first.Id && s.State == JobState.Done);
        }
        finally { holdA.Set(); }
        h.WaitFor(s => s.Name == "cardA1" && s.State == JobState.Done);
    }

    [Fact]
    public void Jobs_sharing_a_drive_run_in_queue_order_and_say_why_they_wait()
    {
        var gates = new GateFileSystem(new WindowsFileSystem());
        using var h = new RunnerHarness(gates, Topology());
        var a = Plan(h, "cardA1", "hddA");
        var b = Plan(h, "cardA2", "ssd");
        var holdA = gates.HoldSourcesUnder(h.Dir.PathOf("cardA1"));
        try
        {
            h.Runner.Start();
            h.Runner.Enqueue(a, OverwritePolicy.Replace, verify: false);
            h.WaitFor(s => s.Name == "cardA1" && s.State == JobState.Running);
            h.Runner.Enqueue(b, OverwritePolicy.Replace, verify: false);

            var waiting = h.WaitFor(s => s.Name == "cardA2" && s.WaitingFor != null);

            Assert.Equal(JobState.Queued, waiting.State);
            Assert.Contains("cardA1", waiting.WaitingFor);
        }
        finally { holdA.Set(); }

        h.WaitFor(s => s.Name == "cardA2" && s.State == JobState.Done);
        var events = h.Events;
        int aDone = Array.FindIndex(events, s => s.Name == "cardA1" && s.State == JobState.Done);
        int bRunning = Array.FindIndex(events, s => s.Name == "cardA2" && s.State == JobState.Running);
        Assert.True(aDone >= 0 && bRunning > aDone, "The second job started before the first one finished with the card.");
    }

    [Fact]
    public void The_card_is_free_while_the_previous_job_verifies()
    {
        var gates = new GateFileSystem(new WindowsFileSystem());
        using var h = new RunnerHarness(gates, Topology());
        var a = Plan(h, "cardA1", "hddA");
        var b = Plan(h, "cardA2", "ssd");
        var holdVerify = gates.HoldVerificationUnder(h.Dir.PathOf("hddA"));
        try
        {
            h.Runner.Start();
            h.Runner.Enqueue(a, OverwritePolicy.Replace, verify: true);
            var first = h.WaitFor(s => s.Name == "cardA1" && s.State == JobState.Verifying);
            h.Runner.Enqueue(b, OverwritePolicy.Replace, verify: true);

            h.WaitFor(s => s.Name == "cardA2" && s.State == JobState.Done);

            Assert.DoesNotContain(h.Events, s => s.Id == first.Id && s.State == JobState.Done);
        }
        finally { holdVerify.Set(); }
        var done = h.WaitFor(s => s.Name == "cardA1" && s.State == JobState.Done);
        Assert.Equal((1, 0), (done.DoneFiles, done.FailedFiles));
    }

    [Fact]
    public void Pausing_one_of_two_running_jobs_leaves_the_other_running()
    {
        var gates = new GateFileSystem(new WindowsFileSystem());
        using var h = new RunnerHarness(gates, Topology());
        var a = Plan(h, "cardA1", "hddA");
        var b = Plan(h, "cardB", "hddB");
        var holdA = gates.HoldSourcesUnder(h.Dir.PathOf("cardA1"));
        var holdB = gates.HoldSourcesUnder(h.Dir.PathOf("cardB"));
        try
        {
            h.Runner.Start();
            h.Runner.Enqueue(a, OverwritePolicy.Replace, verify: true);
            h.Runner.Enqueue(b, OverwritePolicy.Replace, verify: true);
            var first = h.WaitFor(s => s.Name == "cardA1" && s.State == JobState.Running);
            h.WaitFor(s => s.Name == "cardB" && s.State == JobState.Running);

            h.Runner.Pause(first.Id);
            holdA.Set();
            h.WaitFor(s => s.Id == first.Id && s.State == JobState.Paused);
            holdB.Set();

            h.WaitFor(s => s.Name == "cardB" && s.State == JobState.Done);
        }
        finally { holdA.Set(); holdB.Set(); }
        Assert.Empty(Directory.Exists(h.Dir.PathOf("hddA")) ? Directory.GetFiles(h.Dir.PathOf("hddA"), "~tq*.tmp", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public void A_destination_without_enough_space_pauses_the_job()
    {
        using var h = new RunnerHarness(space: new FakeVolumeSpace(free: 1L << 20));
        var plan = Plan(h, "cardA1", "hddA");
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var paused = h.WaitFor(s => s.State == JobState.Paused);

        Assert.StartsWith("Not enough space on", paused.LastError);
        Assert.Equal(0, paused.DoneFiles);
    }
}
