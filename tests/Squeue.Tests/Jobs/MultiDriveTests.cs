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

    /// A drive check that reports paths through the named folder as disconnected once the card is pulled.
    /// (In tests source and destination share one volume, so the check goes by folder, like FolderTopology.)
    private sealed class PullableCard(string folder)
    {
        private volatile bool _pulled;

        public void Pull() => _pulled = true;

        public JobRunnerOptions Options => RunnerHarness.Options with { DriveConnected = Connected };

        private bool Connected(string path) =>
            !_pulled || !Path.GetFullPath(path).Split(Path.DirectorySeparatorChar)
                .Contains(folder, StringComparer.OrdinalIgnoreCase);
    }

    private static JobPlan PlanTwoFiles(RunnerHarness h, string source, string dest)
    {
        h.Dir.Write(Path.Combine(source, "a.bin"), TestDir.RandomBytes(20_000, 1));
        h.Dir.Write(Path.Combine(source, "b.bin"), TestDir.RandomBytes(20_000, 2));
        return JobPlanner.Plan(h.Fs, [h.Dir.PathOf(source)], h.Dir.PathOf(dest));
    }

    [Fact]
    public void Pulling_the_card_during_verification_does_not_pause_the_job()
    {
        var gates = new GateFileSystem(new WindowsFileSystem());
        var card = new PullableCard("cardA1");
        using var h = new RunnerHarness(gates, Topology(), options: card.Options);
        var plan = PlanTwoFiles(h, "cardA1", "hddA");
        var holdVerify = gates.HoldVerificationUnder(h.Dir.PathOf("hddA"));
        JobSnapshot end;
        try
        {
            h.Runner.Start();
            h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
            var verifying = h.WaitFor(s => s.State == JobState.Verifying);

            // The card is pulled, and b.bin's copy is replaced by another program before it is verified:
            // a real per-file failure that has nothing to do with the card.
            card.Pull();
            string b = h.Dir.PathOf(@"hddA\cardA1\b.bin");
            File.Delete(b);
            File.WriteAllBytes(b, [1, 2, 3]);
            holdVerify.Set();

            end = h.WaitFor(s => s.Id == verifying.Id && s.State is JobState.Done or JobState.Paused);
        }
        finally { holdVerify.Set(); }

        Assert.Equal(JobState.Done, end.State);
        Assert.Equal((2, 1, 1), (end.TotalFiles, end.DoneFiles, end.FailedFiles));
        Assert.Contains("destination changed", end.LastError);
        Assert.DoesNotContain(h.Events, s => s.State == JobState.Paused);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(h.Dir.PathOf(@"hddA\cardA1\b.bin")));
    }

    [Fact]
    public void A_copy_that_fails_verification_after_the_card_is_pulled_waits_for_the_card()
    {
        var gates = new GateFileSystem(new WindowsFileSystem());
        var card = new PullableCard("cardA1");
        using var h = new RunnerHarness(gates, Topology(), options: card.Options);
        var plan = PlanTwoFiles(h, "cardA1", "hddA");
        gates.CorruptNextVerificationOf(h.Dir.PathOf(@"hddA\cardA1\b.bin"));
        var holdVerify = gates.HoldVerificationUnder(h.Dir.PathOf("hddA"));
        JobSnapshot paused;
        try
        {
            h.Runner.Start();
            h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
            h.WaitFor(s => s.State == JobState.Verifying);
            card.Pull();
            holdVerify.Set();

            paused = h.WaitFor(s => s.State == JobState.Paused);
        }
        finally { holdVerify.Set(); }

        // b.bin failed verification once, so the protocol copies it again; that needs the card, which is gone.
        Assert.Equal(DriveProblems.NotConnected(Path.GetPathRoot(h.Dir.PathOf("cardA1"))!), paused.LastError);
        Assert.Equal((2, 1, 0), (paused.TotalFiles, paused.DoneFiles, paused.FailedFiles));
        h.Runner.Dispose();
        using var journal = Journal.Open(h.JournalPath);
        var entries = journal.EntriesOf(paused.Id);
        Assert.Equal(new[] { EntryState.Done, EntryState.Pending }, entries.Select(e => e.State));
        Assert.Equal(1, journal.GetEntry(entries[1].Id).VerifyFailures);
    }

    [Fact]
    public void A_job_whose_drives_cannot_be_read_is_paused_and_the_others_still_run()
    {
        using var h = new RunnerHarness();
        // '|' and '<' are accepted by .NET's path functions now; an embedded NUL still makes them throw.
        string badDest = h.Dir.PathOf("backup") + "\0bad";
        string src = h.Dir.Write(@"bad\a.bin", TestDir.RandomBytes(100, 1));
        long badId;
        using (var journal = Journal.Open(h.JournalPath))
        {
            badId = journal.CreateJob(null, "bad", h.Dir.PathOf("bad"), badDest);
            journal.AddEntry(badId, src, badDest + @"\a.bin", 100, ConflictAction.Create, null);
            Assert.Equal(badDest, journal.GetJob(badId).DestRoot); // the journal keeps the NUL
        }
        var good = Plan(h, "card", "backup");
        h.Runner.Start();

        h.Runner.Enqueue(good, OverwritePolicy.Replace, verify: true);
        var paused = h.WaitFor(s => s.Id == badId && s.State == JobState.Paused);
        h.WaitFor(s => s.Name == "card" && s.State == JobState.Done);

        Assert.StartsWith("Squeue couldn't read this job's drives: ", paused.LastError);
    }
}
