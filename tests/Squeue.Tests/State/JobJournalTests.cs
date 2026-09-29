using Microsoft.Data.Sqlite;
using Squeue.Core.State;

namespace Squeue.Tests.State;

public class JobJournalTests
{
    [Fact]
    public void Jobs_store_name_source_destination_and_state()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));

        long id = journal.CreateJob("xxh3", "DCIM", @"F:\DCIM", @"E:\Backup");
        journal.SetJobState(id, JobState.Paused);

        var job = journal.GetJob(id);
        Assert.Equal(("DCIM", @"F:\DCIM", @"E:\Backup"), (job.Name, job.Source, job.DestRoot));
        Assert.Equal((JobState.Paused, "xxh3"), (job.State, job.HashAlgorithm));
        Assert.True(job.CreatedAt > 0);
        Assert.Equal(new[] { id }, journal.Jobs().Select(j => j.Id));
    }

    [Fact]
    public void New_jobs_start_queued()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        Assert.Equal(JobState.Queued, journal.GetJob(journal.CreateJob(null)).State);
    }

    [Fact]
    public void EntriesOf_lists_a_jobs_entries_in_order()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long job = journal.CreateJob(null);
        long other = journal.CreateJob(null);
        long a = journal.AddEntry(job, "a", "A", 10, ConflictAction.Create, null);
        journal.AddEntry(other, "x", "X", 1, ConflictAction.Create, null);
        long b = journal.AddEntry(job, "b", "B", 20, ConflictAction.Create, null);
        journal.SetEntryState(b, EntryState.Failed, "boom");

        var rows = journal.EntriesOf(job);

        Assert.Equal(new[] { a, b }, rows.Select(r => r.Id));
        Assert.Equal(new EntryRow(b, "b", 20, EntryState.Failed, "boom"), rows[1]);
    }

    [Fact]
    public void GetJob_throws_for_an_unknown_id()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        Assert.Throws<KeyNotFoundException>(() => journal.GetJob(99));
    }

    [Fact]
    public void Opens_a_database_created_before_jobs_had_names()
    {
        using var dir = new TestDir();
        string path = dir.PathOf("state.db");
        using (var old = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE jobs (id INTEGER PRIMARY KEY, hash_algo TEXT, created_at INTEGER NOT NULL);
                INSERT INTO jobs (hash_algo, created_at) VALUES ('xxh3', 1);
                """;
            cmd.ExecuteNonQuery();
        }

        using var journal = Journal.Open(path);

        var legacy = journal.Jobs().Single();
        Assert.Equal((JobState.Queued, (string?)null), (legacy.State, legacy.Name));
        long id = journal.CreateJob(null, "new", "src", "dest");
        Assert.Equal("new", journal.GetJob(id).Name);
    }
}
