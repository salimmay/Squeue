using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;
using Squeue.Core.State;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Jobs;

public class JobRunnerTests
{
    private static JobPlan PlanCard(RunnerHarness h, params (string Name, byte[] Content)[] files)
    {
        Directory.CreateDirectory(h.Dir.PathOf("card"));
        foreach (var (name, content) in files) h.Dir.Write(Path.Combine("card", name), content);
        return JobPlanner.Plan(h.Fs, [h.Dir.PathOf("card")], h.Dir.PathOf("backup"));
    }

    private static string[] TempFilesUnder(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "~tq*.tmp", SearchOption.AllDirectories) : [];

    [Fact]
    public void Runs_a_queued_job_to_done()
    {
        using var h = new RunnerHarness();
        var a = TestDir.RandomBytes(100_000, 1);
        var b = TestDir.RandomBytes(5000, 2);
        var plan = PlanCard(h, ("a.bin", a), (@"sub\b.bin", b));
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var done = h.WaitFor(s => s.State == JobState.Done);

        Assert.Equal(("card", 2, 2, 0), (done.Name, done.TotalFiles, done.DoneFiles, done.FailedFiles));
        Assert.Equal(done.TotalBytes, done.DoneBytes);
        Assert.Equal(1.0, done.Fraction);
        Assert.Equal(a, File.ReadAllBytes(h.Dir.PathOf(@"backup\card\a.bin")));
        Assert.Equal(b, File.ReadAllBytes(h.Dir.PathOf(@"backup\card\sub\b.bin")));
    }

    [Fact]
    public void Skip_leaves_existing_files_alone()
    {
        using var h = new RunnerHarness();
        h.Dir.Write(@"backup\card\a.bin", [9]);
        var plan = PlanCard(h, ("a.bin", TestDir.RandomBytes(100)), ("b.bin", TestDir.RandomBytes(100)));
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Skip, verify: true);
        var done = h.WaitFor(s => s.State == JobState.Done);

        // The existing a.bin differs from the source, so it is kept and reported rather than silently skipped.
        Assert.Equal((2, 1, 1), (done.TotalFiles, done.DoneFiles, done.FailedFiles));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(h.Dir.PathOf(@"backup\card\a.bin")));
        Assert.True(File.Exists(h.Dir.PathOf(@"backup\card\b.bin")));
    }

    [Fact]
    public void Skip_reports_different_files_with_the_same_name()
    {
        using var h = new RunnerHarness();
        var same = TestDir.RandomBytes(100, 1);
        var c = TestDir.RandomBytes(100, 3);
        var plan = PlanCard(h, ("a.bin", same), ("b.bin", TestDir.RandomBytes(100, 2)), ("c.bin", c));
        string existingSame = h.Dir.Write(@"backup\card\a.bin", same);
        File.SetLastWriteTimeUtc(existingSame, File.GetLastWriteTimeUtc(h.Dir.PathOf(@"card\a.bin")));
        string existingOther = h.Dir.Write(@"backup\card\b.bin", [4, 2]);
        plan = JobPlanner.Plan(h.Fs, [h.Dir.PathOf("card")], h.Dir.PathOf("backup")); // again, now that both exist
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Skip, verify: true);
        var done = h.WaitFor(s => s.State == JobState.Done);

        Assert.Equal((2, 1, 1), (done.TotalFiles, done.DoneFiles, done.FailedFiles));
        Assert.Equal("A different file with this name is already there. It was not replaced.", done.LastError);
        Assert.Equal(new byte[] { 4, 2 }, File.ReadAllBytes(existingOther));
        Assert.Equal(c, File.ReadAllBytes(h.Dir.PathOf(@"backup\card\c.bin")));
    }

    [Fact]
    public void Pause_stops_mid_file_and_resume_finishes()
    {
        using var h = new RunnerHarness();
        var content = TestDir.RandomBytes(8 * 1024 * 1024);
        var plan = PlanCard(h, ("big.bin", content));
        int paused = 0;
        h.OnEvent = s =>
        {
            if (s.State == JobState.Running && s.DoneBytes > 0 && Interlocked.Exchange(ref paused, 1) == 0) h.Runner.Pause(s.Id);
        };
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var p = h.WaitFor(s => s.State == JobState.Paused);

        string dest = h.Dir.PathOf(@"backup\card\big.bin");
        Assert.False(File.Exists(dest));
        Assert.Empty(TempFilesUnder(h.Dir.PathOf("backup")));

        h.OnEvent = null;
        h.Runner.Resume(p.Id);
        h.WaitFor(s => s.Id == p.Id && s.State == JobState.Done);
        Assert.Equal(content, File.ReadAllBytes(dest));
    }

    [Fact]
    public void Cancel_stops_mid_file_and_leaves_nothing_behind()
    {
        using var h = new RunnerHarness();
        var plan = PlanCard(h, ("big.bin", TestDir.RandomBytes(8 * 1024 * 1024)));
        int cancelled = 0;
        h.OnEvent = s =>
        {
            if (s.State == JobState.Running && s.DoneBytes > 0 && Interlocked.Exchange(ref cancelled, 1) == 0) h.Runner.Cancel(s.Id);
        };
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        h.WaitFor(s => s.State == JobState.Cancelled);

        Assert.False(File.Exists(h.Dir.PathOf(@"backup\card\big.bin")));
        Assert.Empty(TempFilesUnder(h.Dir.PathOf("backup")));
    }

    [Fact]
    public void A_job_left_running_by_a_previous_session_is_finished_on_start()
    {
        using var h = new RunnerHarness();
        var content = TestDir.RandomBytes(10_000);
        string src = h.Dir.Write(@"card\a.bin", content);
        string dest = h.Dir.PathOf(@"backup\a.bin");
        long jobId;
        using (var journal = Journal.Open(h.JournalPath))
        {
            jobId = journal.CreateJob("xxh3", "card", h.Dir.PathOf("card"), h.Dir.PathOf("backup"));
            journal.AddEntry(jobId, src, dest, content.Length, ConflictAction.Create, null);
            journal.SetJobState(jobId, JobState.Running);
        }

        h.Runner.Start();
        h.WaitFor(s => s.Id == jobId && s.State == JobState.Done);

        Assert.Equal(content, File.ReadAllBytes(dest));
    }

    [Fact]
    public void Resume_finishes_a_job_whose_attempt_was_left_unreconciled()
    {
        // The state left when recovery had to wait for a drive: a paused job with an attempt that was never reconciled.
        using var h = new RunnerHarness();
        var content = TestDir.RandomBytes(10_000);
        string src = h.Dir.Write(@"card\a.bin", content);
        string dest = h.Dir.PathOf(@"backup\a.bin");
        long jobId, entryId;
        using (var journal = Journal.Open(h.JournalPath))
        {
            jobId = journal.CreateJob("xxh3", "card", h.Dir.PathOf("card"), h.Dir.PathOf("backup"));
            entryId = journal.AddEntry(jobId, src, dest, content.Length, ConflictAction.Create, null);
            journal.SetJobState(jobId, JobState.Paused);
        }
        h.Runner.Start();
        h.WaitFor(s => s.Id == jobId && s.State == JobState.Paused);
        using (var journal = Journal.Open(h.JournalPath))
        {
            journal.BeginAttempt(entryId, "~tqaaaaaaaaaa.tmp", null);
        }

        h.Runner.Resume(jobId);
        // Wait for Done, or for the job to pause again with a reason (the bug this pins).
        var end = h.WaitFor(s => s.Id == jobId && (s.State == JobState.Done || s.State == JobState.Paused && s.LastError != null));

        Assert.Equal(JobState.Done, end.State);
        Assert.Equal(content, File.ReadAllBytes(dest));
    }

    [Fact]
    public void Replacing_files_always_verifies_even_if_verification_was_turned_off()
    {
        using var h = new RunnerHarness();
        h.Dir.Write(@"backup\card\a.bin", [9]);
        var plan = PlanCard(h, ("a.bin", TestDir.RandomBytes(100)));
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: false);
        var done = h.WaitFor(s => s.State == JobState.Done);
        h.Runner.Dispose();

        using var journal = Journal.Open(h.JournalPath);
        Assert.Equal("xxh3", journal.GetJob(done.Id).HashAlgorithm);
    }

    [Fact]
    public void A_destination_that_appears_mid_job_fails_that_file_and_keeps_it()
    {
        using var h = new RunnerHarness();
        var plan = PlanCard(h, ("a.bin", TestDir.RandomBytes(100)), ("b.bin", TestDir.RandomBytes(100)));
        h.Dir.Write(@"backup\card\a.bin", [7]); // appears after planning
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var done = h.WaitFor(s => s.State == JobState.Done);

        Assert.Equal((2, 1, 1), (done.TotalFiles, done.DoneFiles, done.FailedFiles));
        Assert.Contains("appeared", done.LastError);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(h.Dir.PathOf(@"backup\card\a.bin")));
    }

    [Fact]
    public void Two_verification_failures_fail_the_file_and_the_job_still_finishes()
    {
        using var h = new RunnerHarness(new FaultyFileSystem(new WindowsFileSystem()) { CorruptVerifyReads = true });
        var plan = PlanCard(h, ("a.bin", TestDir.RandomBytes(100_000)));
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var done = h.WaitFor(s => s.State == JobState.Done);

        Assert.Equal(1, done.FailedFiles);
        Assert.Contains("twice", done.LastError);
    }

    [Fact]
    public void An_empty_folder_finishes_immediately()
    {
        using var h = new RunnerHarness();
        var plan = PlanCard(h);
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var done = h.WaitFor(s => s.State == JobState.Done);

        Assert.Equal((0, 1.0), (done.TotalFiles, done.Fraction));
    }

    [Fact]
    public void An_unexpected_error_pauses_the_job_and_resume_finishes_it()
    {
        using var h = new RunnerHarness(new FaultyFileSystem(new WindowsFileSystem()) { CrashAt = (FsOp.OpenForVerify, false, 1) });
        var content = TestDir.RandomBytes(100_000);
        var plan = PlanCard(h, ("a.bin", content));
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var p = h.WaitFor(s => s.State == JobState.Paused);

        Assert.Contains("Simulated crash", p.LastError);
        h.Runner.Resume(p.Id);
        h.WaitFor(s => s.Id == p.Id && s.State == JobState.Done);
        Assert.Equal(content, File.ReadAllBytes(h.Dir.PathOf(@"backup\card\a.bin")));
        Assert.Empty(TempFilesUnder(h.Dir.PathOf("backup")));
    }

    private static void A_drive_error_pauses_the_job_and_resume_finishes(int win32Error, string reason)
    {
        using var h = new RunnerHarness(new ThrowingFileSystem(new WindowsFileSystem(), win32Error, failOnOpenSourceNumber: 2));
        var files = new[] { ("a.bin", TestDir.RandomBytes(1000, 1)), ("b.bin", TestDir.RandomBytes(1000, 2)), ("c.bin", TestDir.RandomBytes(1000, 3)) };
        var plan = PlanCard(h, files);
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var p = h.WaitFor(s => s.State == JobState.Paused);

        Assert.Contains(reason, p.LastError);
        Assert.Equal((3, 1, 0), (p.TotalFiles, p.DoneFiles, p.FailedFiles));
        using (var journal = Journal.Open(h.JournalPath))
        {
            Assert.Equal(new[] { EntryState.Done, EntryState.Pending, EntryState.Pending }, journal.EntriesOf(p.Id).Select(e => e.State));
        }

        h.Runner.Resume(p.Id);
        var done = h.WaitFor(s => s.Id == p.Id && s.State == JobState.Done);

        Assert.Equal((3, 3, 0), (done.TotalFiles, done.DoneFiles, done.FailedFiles));
        foreach (var (name, content) in files) Assert.Equal(content, File.ReadAllBytes(h.Dir.PathOf($@"backup\card\{name}")));
        Assert.Empty(TempFilesUnder(h.Dir.PathOf("backup")));
    }

    [Fact]
    public void A_full_destination_pauses_the_job_and_fails_nothing() =>
        A_drive_error_pauses_the_job_and_resume_finishes(112, "full");

    [Fact]
    public void A_device_error_pauses_the_job() =>
        A_drive_error_pauses_the_job_and_resume_finishes(1167, "stopped responding");

    [Fact]
    public void A_job_whose_drive_is_missing_is_paused_not_failed()
    {
        using var h = new RunnerHarness();
        string missing = TestDir.MissingDriveRoot();
        var content = TestDir.RandomBytes(1000);
        string src = h.Dir.Write(@"card\a.bin", content);
        long jobId;
        using (var journal = Journal.Open(h.JournalPath))
        {
            jobId = journal.CreateJob("xxh3", "card", h.Dir.PathOf("card"), missing + "Backup");
            journal.AddEntry(jobId, src, missing + @"Backup\card\a.bin", content.Length, ConflictAction.Create, null);
        }

        h.Runner.Start();
        var p = h.WaitFor(s => s.Id == jobId && s.State == JobState.Paused);

        Assert.Equal($"{missing} isn't connected. Reconnect it and resume.", p.LastError);
        Assert.Equal((0, 0), (p.DoneFiles, p.FailedFiles));
        h.Runner.Dispose();
        using (var journal = Journal.Open(h.JournalPath))
        {
            Assert.Equal(EntryState.Pending, journal.EntriesOf(jobId).Single().State);
        }
    }

    [Fact]
    public void A_job_queued_during_a_big_file_appears_immediately()
    {
        using var h = new RunnerHarness();
        var big = PlanCard(h, ("big.bin", TestDir.RandomBytes(8 * 1024 * 1024)));
        h.Dir.Write(Path.Combine("card2", "x.bin"), TestDir.RandomBytes(100));
        var second = JobPlanner.Plan(h.Fs, [h.Dir.PathOf("card2")], h.Dir.PathOf("backup"));
        int enqueued = 0;
        h.OnEvent = s =>
        {
            if (s.State == JobState.Running && s.DoneBytes > 0 && Interlocked.Exchange(ref enqueued, 1) == 0)
                h.Runner.Enqueue(second, OverwritePolicy.Replace, verify: true);
        };
        h.Runner.Start();

        h.Runner.Enqueue(big, OverwritePolicy.Replace, verify: true);
        var firstDone = h.WaitFor(s => s.Name == "card" && s.State == JobState.Done);
        h.WaitFor(s => s.Name == "card2" && s.State == JobState.Done);

        var events = h.Events.ToList();
        int queued = events.FindIndex(s => s.Name == "card2" && s.State == JobState.Queued);
        int done = events.FindIndex(s => s.Id == firstDone.Id && s.State == JobState.Done);
        Assert.InRange(queued, 0, done - 1);
        // It arrived while the big file was still copying, not after it finished.
        Assert.Contains(events.Skip(queued + 1), s => s.Id == firstDone.Id && s.State == JobState.Running && s.DoneBytes < s.TotalBytes);
    }

    [Fact]
    public void Startup_shows_unfinished_jobs_and_todays_finished_ones_only()
    {
        using var h = new RunnerHarness();
        long oldDone, todayDone, oldPaused;
        using (var journal = Journal.Open(h.JournalPath))
        {
            oldDone = journal.CreateJob(null, "old");
            todayDone = journal.CreateJob(null, "today");
            oldPaused = journal.CreateJob(null, "paused");
            journal.SetJobState(oldDone, JobState.Done);
            journal.SetJobState(todayDone, JobState.Done);
            journal.SetJobState(oldPaused, JobState.Paused);
        }
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={h.JournalPath};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE jobs SET created_at = $old WHERE id IN ($a, $b)";
            cmd.Parameters.AddWithValue("$old", DateTime.UtcNow.AddDays(-3).ToFileTimeUtc());
            cmd.Parameters.AddWithValue("$a", oldDone);
            cmd.Parameters.AddWithValue("$b", oldPaused);
            cmd.ExecuteNonQuery();
        }

        h.Runner.Start();
        h.WaitFor(s => s.Id == oldPaused);

        Assert.Equal(new[] { todayDone, oldPaused }, h.Events.Select(s => s.Id));
    }

    [Fact]
    public void Two_jobs_run_in_order()
    {
        using var h = new RunnerHarness();
        var first = PlanCard(h, ("a.bin", TestDir.RandomBytes(300_000, 1)));
        var b = TestDir.RandomBytes(1000, 2);
        h.Dir.Write(Path.Combine("card2", "b.bin"), b);
        var second = JobPlanner.Plan(h.Fs, [h.Dir.PathOf("card2")], h.Dir.PathOf("backup"));

        h.Runner.Enqueue(first, OverwritePolicy.Replace, verify: true);
        h.Runner.Enqueue(second, OverwritePolicy.Replace, verify: true);
        h.Runner.Start();
        h.WaitFor(s => s.Name == "card2" && s.State == JobState.Done);

        var events = h.Events.ToList();
        int firstDone = events.FindIndex(s => s.Name == "card" && s.State == JobState.Done);
        int secondStarts = events.FindIndex(s => s.Name == "card2" && s.State == JobState.Running);
        Assert.InRange(firstDone, 0, secondStarts - 1);
        Assert.DoesNotContain(events.Skip(secondStarts), s => s.Name == "card" && s.State == JobState.Running);
        Assert.Equal(b, File.ReadAllBytes(h.Dir.PathOf(@"backup\card2\b.bin")));
    }

    [Fact]
    public void A_paused_job_stays_paused_after_a_restart()
    {
        using var h = new RunnerHarness();
        var plan = PlanCard(h, ("big.bin", TestDir.RandomBytes(8 * 1024 * 1024)));
        int paused = 0;
        h.OnEvent = s =>
        {
            if (s.State == JobState.Running && s.DoneBytes > 0 && Interlocked.Exchange(ref paused, 1) == 0) h.Runner.Pause(s.Id);
        };
        h.Runner.Start();
        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var p = h.WaitFor(s => s.State == JobState.Paused);
        h.OnEvent = null;

        h.Restart();
        h.Dir.Write(Path.Combine("card2", "x.bin"), TestDir.RandomBytes(100));
        h.Runner.Enqueue(JobPlanner.Plan(h.Fs, [h.Dir.PathOf("card2")], h.Dir.PathOf("backup")), OverwritePolicy.Replace, verify: true);
        h.WaitFor(s => s.Name == "card2" && s.State == JobState.Done);

        Assert.Equal(JobState.Paused, h.WaitFor(s => s.Id == p.Id).State);
        Assert.DoesNotContain(h.Events, s => s.Id == p.Id && s.State != JobState.Paused);
        Assert.False(File.Exists(h.Dir.PathOf(@"backup\card\big.bin")));
        Assert.Empty(TempFilesUnder(h.Dir.PathOf("backup")));
    }

    [Fact]
    public void A_subscriber_that_throws_does_not_stop_the_runner()
    {
        using var h = new RunnerHarness();
        h.OnEvent = _ => throw new InvalidOperationException("boom");
        var plan = PlanCard(h, ("a.bin", TestDir.RandomBytes(100)));
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);

        h.WaitFor(s => s.State == JobState.Done);
    }

    [Fact]
    public void Commands_after_dispose_are_ignored_and_dispose_can_be_called_twice()
    {
        using var h = new RunnerHarness();
        h.Runner.Start();
        h.Runner.Dispose();

        h.Runner.Pause(1);
        h.Runner.Resume(1);
        h.Runner.Cancel(1);
        h.Runner.Dispose();

        Assert.Throws<ObjectDisposedException>(() => h.Runner.Start());
    }
}
