using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Tests.State;

public class JournalTests
{
    private static readonly FileIdentity Sample = new(7, (UInt128)123456789, 42, 1, 2, 3, 0x20);

    [Fact]
    public void Uses_wal_and_full_synchronous_mode()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        Assert.Equal("wal", journal.Pragma("journal_mode"));
        Assert.Equal("2", journal.Pragma("synchronous"));
    }

    [Fact]
    public void Entry_round_trips_with_identities_and_hashes()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long job = journal.CreateJob("xxh3");
        long id = journal.AddEntry(job, @"C:\src\a.bin", @"D:\dest\a.bin", 42, ConflictAction.Replace, Sample);

        journal.RecordSourceVersion(id, Sample with { Size = 43 });
        journal.SetHashes(id, "aa", null);
        journal.SetHashes(id, null, "bb");
        journal.SetEntryState(id, EntryState.Failed, "boom");
        var entry = journal.GetEntry(id);

        Assert.Equal(@"D:\dest", entry.DestDirectory);
        Assert.Equal(ConflictAction.Replace, entry.Action);
        Assert.Equal(Sample, entry.SeenDest);
        Assert.Equal(43, entry.SourceVersion!.Value.Size);
        Assert.Equal("xxh3", entry.HashAlgorithm);
        Assert.Equal(("aa", "bb"), (entry.SrcHash, entry.DestHash));
        Assert.Equal((EntryState.Failed, "boom"), (entry.State, entry.Error));
    }

    [Fact]
    public void Finished_and_abandoned_attempts_are_no_longer_open()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long entry = journal.AddEntry(journal.CreateJob(null), "a", "b", 1, ConflictAction.Create, null);

        long first = journal.BeginAttempt(entry, "~tqaaaaaaaaaa.tmp", null);
        journal.SetPhase(first, AttemptPhase.TempCreated, tempFileId: (UInt128)5);
        Assert.Equal(first, journal.GetOpenAttempt(entry)!.Id);
        Assert.Equal((UInt128)5, journal.GetOpenAttempt(entry)!.TempFileId);

        journal.SetPhase(first, AttemptPhase.Abandoned);
        long second = journal.BeginAttempt(entry, "~tqbbbbbbbbbb.tmp", (UInt128)9);
        journal.SetPhase(second, AttemptPhase.Published, publishedFileId: (UInt128)6);

        Assert.Single(journal.OpenAttempts());
        var open = journal.GetOpenAttempt(entry)!;
        Assert.Equal((second, AttemptPhase.Published), (open.Id, open.Phase));
        Assert.Equal(((UInt128)9, (UInt128)6), (open.ReplacedFileId!.Value, open.PublishedFileId!.Value));

        journal.SetPhase(second, AttemptPhase.Finished);
        Assert.Empty(journal.OpenAttempts());
        Assert.Equal(2, journal.AttemptsFor(entry).Count);
    }

    [Fact]
    public void State_survives_closing_and_reopening()
    {
        using var dir = new TestDir();
        string path = dir.PathOf("state.db");
        long entry;
        using (var journal = Journal.Open(path))
        {
            entry = journal.AddEntry(journal.CreateJob("xxh3"), "a", "b", 1, ConflictAction.Create, null);
            journal.BeginAttempt(entry, "~tqaaaaaaaaaa.tmp", null);
        }

        using var reopened = Journal.Open(path);
        Assert.Equal("a", reopened.GetEntry(entry).SrcPath);
        Assert.Single(reopened.OpenAttempts());
    }

    [Fact]
    public void Committed_state_is_visible_from_a_second_connection_without_closing_the_first()
    {
        using var dir = new TestDir();
        string path = dir.PathOf("state.db");
        long entry;
        using var journalA = Journal.Open(path);
        {
            entry = journalA.AddEntry(journalA.CreateJob("xxh3"), "a", "b", 1, ConflictAction.Create, null);
            journalA.BeginAttempt(entry, "~tqaaaaaaaaaa.tmp", null);

            using var journalB = Journal.Open(path);
            Assert.Equal("a", journalB.GetEntry(entry).SrcPath);
            Assert.Single(journalB.OpenAttempts());
        }
    }

    [Fact]
    public void RecordVerifyFailure_switches_to_replacing_the_published_file()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long id = journal.AddEntry(journal.CreateJob("xxh3"), "a", "b", 1, ConflictAction.Create, null);
        journal.SetHashes(id, "aa", "bb");

        Assert.Equal(1, journal.RecordVerifyFailure(id, Sample));
        var entry = journal.GetEntry(id);
        Assert.Equal(ConflictAction.Replace, entry.Action);
        Assert.Equal(Sample, entry.SeenDest);
        Assert.Null(entry.SrcHash);
        Assert.Null(entry.DestHash);

        Assert.Equal(2, journal.RecordVerifyFailure(id, null));
        Assert.Equal(Sample, journal.GetEntry(id).SeenDest);
    }
}
