using Microsoft.Data.Sqlite;
using Squeue.Core.State;

namespace Squeue.Tests.State;

public class JobHistoryJournalTests
{
    [Fact]
    public void FinishJob_stores_state_summary_and_time()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long id = journal.CreateJob(null);

        journal.FinishJob(id, JobState.Done, new JobSummary(3, 2, 1, 500, "boom"));

        var job = journal.GetJob(id);
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(new JobSummary(3, 2, 1, 500, "boom"), job.Summary);
        Assert.True(job.FinishedAt > 0);
    }

    [Fact]
    public void A_job_set_back_to_queued_loses_its_summary()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long id = journal.CreateJob(null);
        journal.FinishJob(id, JobState.Done, new JobSummary(3, 2, 1, 500, "boom"));

        journal.SetJobState(id, JobState.Queued);

        var job = journal.GetJob(id);
        Assert.Null(job.Summary);
        Assert.Null(job.FinishedAt);
    }

    [Fact]
    public void FailedEntriesOf_lists_failed_files_up_to_the_limit()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long job = journal.CreateJob(null);
        long a = journal.AddEntry(job, "a", "A", 1, ConflictAction.Create, null);
        long b = journal.AddEntry(job, "b", "B", 1, ConflictAction.Create, null);
        long c = journal.AddEntry(job, "c", "C", 1, ConflictAction.Create, null);
        journal.SetEntryState(a, EntryState.Failed, "first");
        journal.SetEntryState(b, EntryState.Done);
        journal.SetEntryState(c, EntryState.Failed, "second");

        Assert.Equal(new[] { a }, journal.FailedEntriesOf(job, 1).Select(e => e.Id));
        var both = journal.FailedEntriesOf(job, 10);
        Assert.Equal(new[] { a, c }, both.Select(e => e.Id));
        Assert.Equal(new[] { "first", "second" }, both.Select(e => e.Error));
    }

    [Fact]
    public void RetryFailedEntries_puts_only_failed_files_back_in_line()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long job = journal.CreateJob(null);
        long done = journal.AddEntry(job, "a", "A", 1, ConflictAction.Create, null);
        long failed = journal.AddEntry(job, "b", "B", 1, ConflictAction.Create, null);
        journal.SetEntryState(done, EntryState.Done);
        journal.RecordVerifyFailure(failed, null);
        journal.RecordVerifyFailure(failed, null);
        journal.SetEntryState(failed, EntryState.Failed, "mismatch");
        Assert.Equal(2, journal.GetEntry(failed).VerifyFailures);

        int count = journal.RetryFailedEntries(job);

        Assert.Equal(1, count);
        var retried = journal.GetEntry(failed);
        Assert.Equal((EntryState.Pending, (string?)null, 0), (retried.State, retried.Error, retried.VerifyFailures));
        Assert.Equal(EntryState.Done, journal.GetEntry(done).State);
    }

    [Fact]
    public void Prune_removes_only_old_finished_jobs_without_open_attempts()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long a = journal.CreateJob(null);
        long entryA = journal.AddEntry(a, "a", "A", 1, ConflictAction.Create, null);
        long attemptA = journal.BeginAttempt(entryA, "a.tmp", null);
        journal.SetPhase(attemptA, AttemptPhase.Finished);
        journal.FinishJob(a, JobState.Done, new JobSummary(1, 1, 0, 1, null));
        long b = journal.CreateJob(null);
        long entryB = journal.AddEntry(b, "b", "B", 1, ConflictAction.Create, null);
        journal.BeginAttempt(entryB, "b.tmp", null);
        journal.FinishJob(b, JobState.Done, new JobSummary(1, 0, 1, 1, "x"));
        long c = journal.CreateJob(null);

        int removed = journal.PruneFinishedBefore(DateTime.UtcNow.AddDays(1).ToFileTimeUtc());

        Assert.Equal(1, removed);
        Assert.Equal(new[] { b, c }, journal.Jobs().Select(j => j.Id));
        Assert.Empty(journal.EntriesOf(a));
        Assert.Empty(journal.AttemptsFor(entryA));
    }

    [Fact]
    public void Opens_a_database_without_job_summaries()
    {
        using var dir = new TestDir();
        string path = dir.PathOf("state.db");
        using (var old = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE jobs (id INTEGER PRIMARY KEY, hash_algo TEXT, name TEXT, source TEXT, dest_root TEXT,
                                   state TEXT NOT NULL DEFAULT 'Queued', created_at INTEGER NOT NULL);
                INSERT INTO jobs (hash_algo, name, state, created_at) VALUES ('xxh3', 'old', 'Done', 1);
                """;
            cmd.ExecuteNonQuery();
        }

        using var journal = Journal.Open(path);

        var legacy = journal.Jobs().Single();
        Assert.Null(legacy.Summary);
        journal.FinishJob(legacy.Id, JobState.Done, new JobSummary(1, 1, 0, 10, null));
        Assert.Equal(new JobSummary(1, 1, 0, 10, null), journal.GetJob(legacy.Id).Summary);
    }
}
