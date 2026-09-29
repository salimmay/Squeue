using Squeue.Core.State;

namespace Squeue.Tests.State;

public class JournalConcurrencyTests
{
    [Fact]
    public async Task Two_connections_can_write_at_the_same_time()
    {
        using var dir = new TestDir();
        string path = dir.PathOf("state.db");
        long job;
        using (var setup = Journal.Open(path)) job = setup.CreateJob(null);

        void Writer(string prefix)
        {
            using var journal = Journal.Open(path);
            for (int i = 0; i < 200; i++)
            {
                long entry = journal.AddEntry(job, $"{prefix}{i}", $"{prefix}{i}", 1, ConflictAction.Create, null);
                journal.Atomically(() =>
                {
                    long attempt = journal.BeginAttempt(entry, "~tqaaaaaaaaaa.tmp", null);
                    journal.SetEntryState(entry, EntryState.Active);
                    journal.SetPhase(attempt, AttemptPhase.Abandoned);
                });
            }
        }

        var a = Task.Run(() => Writer("a"));
        var b = Task.Run(() => Writer("b"));
        await Task.WhenAll(a, b);

        using var check = Journal.Open(path);
        Assert.Equal(400, check.EntriesOf(job).Count);
        Assert.All(check.EntriesOf(job), e => Assert.Equal(EntryState.Active, e.State));
    }

    [Fact]
    public void Verifying_is_a_job_state()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long job = journal.CreateJob(null);
        journal.SetJobState(job, JobState.Verifying);
        Assert.Equal(JobState.Verifying, journal.GetJob(job).State);
    }

    [Fact]
    public async Task A_writer_waits_for_another_connections_transaction_then_succeeds()
    {
        using var dir = new TestDir();
        string path = dir.PathOf("state.db");
        long job;
        using (var setup = Journal.Open(path)) job = setup.CreateJob(null);

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var aTask = Task.Run(() =>
        {
            using var a = Journal.Open(path);
            a.Atomically(() =>
            {
                a.SetJobState(job, JobState.Running);
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            });
        });
        Assert.True(held.Wait(TimeSpan.FromSeconds(10)));

        var bTask = Task.Run(() =>
        {
            using var b = Journal.Open(path);
            b.Atomically(() =>
            {
                _ = b.GetJob(job);
                b.SetJobState(job, JobState.Paused);
            });
        });

        await Task.Delay(300);
        Assert.False(bTask.IsCompleted);

        release.Set();
        var all = Task.WhenAll(aTask, bTask);
        Assert.Same(all, await Task.WhenAny(all, Task.Delay(15000)));
        await all;

        using var check = Journal.Open(path);
        Assert.Equal(JobState.Paused, check.GetJob(job).State);
    }
}
