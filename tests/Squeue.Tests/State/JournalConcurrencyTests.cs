using Squeue.Core.State;

namespace Squeue.Tests.State;

public class JournalConcurrencyTests
{
    [Fact]
    public void Two_connections_can_write_at_the_same_time()
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
        Task.WaitAll(a, b);

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
}
