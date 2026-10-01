using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;
using Squeue.Core.State;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Jobs;

public class JobHistoryTests
{
    private const string DifferentFile = "A different file with this name is already there. It was not replaced.";

    private static JobPlan Plan(RunnerHarness h, string folder, params (string Name, byte[] Content)[] files)
    {
        Directory.CreateDirectory(h.Dir.PathOf(folder));
        foreach (var (name, content) in files) h.Dir.Write(Path.Combine(folder, name), content);
        return JobPlanner.Plan(h.Fs, [h.Dir.PathOf(folder)], h.Dir.PathOf("backup"));
    }

    /// A finished job with one copied file (b.bin) and one failed (a.bin: a different a.bin was already there).
    private static JobSnapshot RunJobWithOneFailedFile(RunnerHarness h)
    {
        h.Dir.Write(@"backup\card\a.bin", [9]);
        var plan = Plan(h, "card", ("a.bin", TestDir.RandomBytes(100, 1)), ("b.bin", TestDir.RandomBytes(100, 2)));
        h.Runner.Start();
        h.Runner.Enqueue(plan, OverwritePolicy.Skip, verify: true);
        var done = h.WaitFor(s => s.State == JobState.Done);
        Assert.Equal((2, 1, 1), (done.TotalFiles, done.DoneFiles, done.FailedFiles));
        return done;
    }

    /// Runs a small job to Done. Commands are handled in order, so everything sent before it has been handled by then.
    private static void RunMarkerJob(RunnerHarness h)
    {
        h.Runner.Enqueue(Plan(h, "marker", ("m.bin", TestDir.RandomBytes(100, 7))), OverwritePolicy.Replace, verify: true);
        h.WaitFor(s => s.Name == "marker" && s.State == JobState.Done);
    }

    [Fact]
    public void A_finished_job_carries_its_finish_time_and_checked_flag()
    {
        using var h = new RunnerHarness();
        h.Runner.Start();

        h.Runner.Enqueue(Plan(h, "card", ("a.bin", TestDir.RandomBytes(1000))), OverwritePolicy.Replace, verify: true);
        var verified = h.WaitFor(s => s.Name == "card" && s.State == JobState.Done);
        h.Runner.Enqueue(Plan(h, "card2", ("b.bin", TestDir.RandomBytes(1000))), OverwritePolicy.Replace, verify: false);
        var unverified = h.WaitFor(s => s.Name == "card2" && s.State == JobState.Done);

        Assert.NotNull(verified.FinishedAtUtc);
        Assert.InRange(verified.FinishedAtUtc.Value, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.True(verified.Checked);
        Assert.False(unverified.Checked);
        // Until it finishes, a job has no finish time.
        Assert.All(h.Events.Where(s => s.State is not (JobState.Done or JobState.Cancelled)), s => Assert.Null(s.FinishedAtUtc));
        Assert.All(h.Events.Where(s => s.Name == "card"), s => Assert.True(s.Checked));
    }

    [Fact]
    public void Finished_jobs_are_listed_again_after_a_restart()
    {
        using var h = new RunnerHarness();
        var before = RunJobWithOneFailedFile(h);

        h.Restart();
        var after = h.WaitFor(s => s.Id == before.Id);

        Assert.Equal(JobState.Done, after.State);
        Assert.Equal((before.TotalFiles, before.DoneFiles, before.FailedFiles, before.TotalBytes, before.DoneBytes),
            (after.TotalFiles, after.DoneFiles, after.FailedFiles, after.TotalBytes, after.DoneBytes));
        Assert.NotNull(after.FinishedAtUtc);
        Assert.Equal(before.FinishedAtUtc, after.FinishedAtUtc);
        Assert.Equal((before.LastError, before.Checked, before.CreatedAtUtc), (after.LastError, after.Checked, after.CreatedAtUtc));
    }

    [Fact]
    public void Old_finished_jobs_get_a_summary_at_startup()
    {
        using var h = new RunnerHarness();
        long jobId;
        using (var journal = Journal.Open(h.JournalPath))
        {
            jobId = journal.CreateJob("xxh3", "old", h.Dir.PathOf("card"), h.Dir.PathOf("backup"));
            long entryId = journal.AddEntry(jobId, h.Dir.PathOf(@"card\a.bin"), h.Dir.PathOf(@"backup\a.bin"), 100, ConflictAction.Create, null);
            journal.SetEntryState(entryId, EntryState.Done);
            journal.SetJobState(jobId, JobState.Done);
            Assert.Null(journal.GetJob(jobId).Summary);
        }

        h.Runner.Start();
        var shown = h.WaitFor(s => s.Id == jobId);

        Assert.Equal((JobState.Done, 1, 1, 0), (shown.State, shown.TotalFiles, shown.DoneFiles, shown.FailedFiles));
        Assert.Equal((100L, 100L), (shown.TotalBytes, shown.DoneBytes));
        Assert.NotNull(shown.FinishedAtUtc);
        h.Runner.Dispose();
        using (var journal = Journal.Open(h.JournalPath))
        {
            var job = journal.GetJob(jobId);
            Assert.Equal(new JobSummary(1, 1, 0, 100, null), job.Summary);
            Assert.NotNull(job.FinishedAt); // so it can be pruned once it is old enough
        }
    }

    [Fact]
    public void Jobs_older_than_the_history_window_are_removed_at_startup()
    {
        using var h = new RunnerHarness(options: RunnerHarness.Options with { HistoryDays = 1 });
        long oldId, recentId;
        using (var journal = Journal.Open(h.JournalPath))
        {
            oldId = journal.CreateJob(null, "old");
            journal.FinishJob(oldId, JobState.Done, new JobSummary(0, 0, 0, 0, null));
            recentId = journal.CreateJob(null, "recent");
            journal.FinishJob(recentId, JobState.Done, new JobSummary(0, 0, 0, 0, null));
        }
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={h.JournalPath};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE jobs SET finished_at = $old WHERE id = $id";
            cmd.Parameters.AddWithValue("$old", DateTime.UtcNow.AddDays(-3).ToFileTimeUtc());
            cmd.Parameters.AddWithValue("$id", oldId);
            cmd.ExecuteNonQuery();
        }

        h.Runner.Start();
        RunMarkerJob(h);

        Assert.DoesNotContain(h.Events, s => s.Id == oldId);
        Assert.Contains(h.Events, s => s.Id == recentId && s.State == JobState.Done);
        h.Runner.Dispose();
        using (var journal = Journal.Open(h.JournalPath))
        {
            Assert.DoesNotContain(journal.Jobs(), j => j.Id == oldId);
            Assert.Contains(journal.Jobs(), j => j.Id == recentId);
        }
    }

    [Fact]
    public void RetryFailed_copies_only_the_failed_files()
    {
        using var h = new RunnerHarness();
        var first = RunJobWithOneFailedFile(h);
        File.Delete(h.Dir.PathOf(@"backup\card\a.bin"));

        h.Runner.RetryFailed(first.Id);
        var done = h.WaitFor(s => s.Id == first.Id && s.State == JobState.Done && s.FailedFiles == 0);

        Assert.Equal((2, 2, 0), (done.TotalFiles, done.DoneFiles, done.FailedFiles));
        Assert.Null(done.LastError);
        Assert.Equal(File.ReadAllBytes(h.Dir.PathOf(@"card\a.bin")), File.ReadAllBytes(h.Dir.PathOf(@"backup\card\a.bin")));
        // It went back through the queue rather than straight to Done.
        var events = h.Events.Where(s => s.Id == first.Id).ToList();
        int firstDone = events.FindIndex(s => s.State == JobState.Done);
        Assert.Equal(JobState.Queued, events[firstDone + 1].State);
        Assert.Null(events[firstDone + 1].FinishedAtUtc);
        h.Runner.Dispose();
        using var journal = Journal.Open(h.JournalPath);
        var entries = journal.EntriesOf(first.Id);
        Assert.All(entries, e => Assert.Equal(EntryState.Done, e.State));
        var untouched = entries.Single(e => e.SrcPath.EndsWith("b.bin"));
        Assert.Single(journal.AttemptsFor(untouched.Id)); // copied once, not again by the retry
    }

    [Fact]
    public void RetryFailed_does_nothing_for_a_job_without_failures()
    {
        using var h = new RunnerHarness();
        h.Runner.Start();
        h.Runner.Enqueue(Plan(h, "card", ("a.bin", TestDir.RandomBytes(1000))), OverwritePolicy.Replace, verify: true);
        var done = h.WaitFor(s => s.Name == "card" && s.State == JobState.Done);
        Assert.Equal(0, done.FailedFiles);

        h.Runner.RetryFailed(done.Id);
        RunMarkerJob(h);

        var events = h.Events.Where(s => s.Id == done.Id).ToList();
        Assert.Equal(JobState.Done, events[^1].State);
        Assert.Single(events, s => s.State == JobState.Done); // nothing was published for it after it finished
        h.Runner.Dispose();
        using var journal = Journal.Open(h.JournalPath);
        Assert.Equal(JobState.Done, journal.GetJob(done.Id).State);
        Assert.NotNull(journal.GetJob(done.Id).Summary);
    }

    [Fact]
    public void RetryFailed_does_nothing_for_a_cancelled_job()
    {
        using var h = new RunnerHarness();
        string src = h.Dir.Write(@"card\a.bin", TestDir.RandomBytes(100));
        long jobId, entryId;
        using (var journal = Journal.Open(h.JournalPath))
        {
            jobId = journal.CreateJob("xxh3", "card", h.Dir.PathOf("card"), h.Dir.PathOf("backup"));
            entryId = journal.AddEntry(jobId, src, h.Dir.PathOf(@"backup\card\a.bin"), 100, ConflictAction.Create, null);
            journal.SetEntryState(entryId, EntryState.Failed, "It could not be read.");
            journal.FinishJob(jobId, JobState.Cancelled, new JobSummary(1, 0, 1, 100, "It could not be read."));
        }
        h.Runner.Start();
        h.WaitFor(s => s.Id == jobId && s.State == JobState.Cancelled);

        h.Runner.RetryFailed(jobId);
        RunMarkerJob(h);

        Assert.Single(h.Events, s => s.Id == jobId); // still only the startup snapshot: never Queued or Running
        Assert.False(File.Exists(h.Dir.PathOf(@"backup\card\a.bin")));
        h.Runner.Dispose();
        using (var journal = Journal.Open(h.JournalPath))
        {
            Assert.Equal(JobState.Cancelled, journal.GetJob(jobId).State);
            Assert.Equal(EntryState.Failed, journal.GetEntry(entryId).State);
        }
    }

    [Fact]
    public void Failed_files_can_be_requested()
    {
        using var h = new RunnerHarness();
        var done = RunJobWithOneFailedFile(h);

        h.Runner.RequestFailedFiles(done.Id);
        var files = h.WaitForFailedFiles(done.Id);

        var file = Assert.Single(files);
        Assert.EndsWith(@"card\a.bin", file.Path);
        Assert.Equal(DifferentFile, file.Reason);
    }

    [Fact]
    public void A_cancelled_job_is_kept_in_history()
    {
        // Two jobs reading the same pretend card: the first is held at its first read, so the second stays queued.
        var gates = new GateFileSystem(new WindowsFileSystem());
        using var h = new RunnerHarness(gates, new FolderTopology(("cardA1", "card-a"), ("cardA2", "card-a")));
        var running = Plan(h, "cardA1", ("a.bin", TestDir.RandomBytes(1000, 1)));
        var queued = Plan(h, "cardA2", ("b.bin", TestDir.RandomBytes(1000, 2)));
        var hold = gates.HoldSourcesUnder(h.Dir.PathOf("cardA1"));
        JobSnapshot first, second;
        try
        {
            h.Runner.Start();
            h.Runner.Enqueue(running, OverwritePolicy.Replace, verify: true);
            first = h.WaitFor(s => s.Name == "cardA1" && s.State == JobState.Running);
            h.Runner.Enqueue(queued, OverwritePolicy.Replace, verify: true);
            second = h.WaitFor(s => s.Name == "cardA2" && s.WaitingFor != null);

            // Cancelled while queued: it has no worker, so it is finished at once.
            h.Runner.Cancel(second.Id);
            var cancelledQueued = h.WaitFor(s => s.Id == second.Id && s.State == JobState.Cancelled);
            Assert.NotNull(cancelledQueued.FinishedAtUtc);
            Assert.InRange(cancelledQueued.FinishedAtUtc.Value, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddSeconds(1));
            Assert.Equal((1, 0, 0), (cancelledQueued.TotalFiles, cancelledQueued.DoneFiles, cancelledQueued.FailedFiles));

            // Cancelled while running: it is reported, with its finish time, once its worker has stopped.
            h.Runner.Cancel(first.Id);
        }
        finally { hold.Set(); }
        var cancelledRunning = h.WaitFor(s => s.Id == first.Id && s.State == JobState.Cancelled);
        Assert.NotNull(cancelledRunning.FinishedAtUtc);

        h.Restart();
        Assert.Equal(JobState.Cancelled, h.WaitFor(s => s.Id == second.Id).State);
        Assert.Equal(cancelledRunning.FinishedAtUtc, h.WaitFor(s => s.Id == first.Id).FinishedAtUtc);
        h.Runner.Dispose();
        using var journal = Journal.Open(h.JournalPath);
        Assert.All(journal.Jobs(), j => Assert.NotNull(j.Summary));
    }
}
