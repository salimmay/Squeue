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

        Assert.Equal(1, done.TotalFiles);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(h.Dir.PathOf(@"backup\card\a.bin")));
        Assert.True(File.Exists(h.Dir.PathOf(@"backup\card\b.bin")));
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
}
