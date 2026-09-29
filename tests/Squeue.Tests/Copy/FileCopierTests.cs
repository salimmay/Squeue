using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Copy;

public class FileCopierTests
{
    private const int Chunk = 64 * 1024;
    private static readonly CopyOptions Options = new() { ChunkSize = Chunk, SharingRetryTimeout = TimeSpan.Zero };
    private readonly WindowsFileSystem _fs = new();

    [Fact]
    public void Copies_a_new_file_and_verifies_it()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(3 * Chunk + 17);
        s.WriteSource(content);
        long id = s.AddEntry();

        var result = new FileCopier(_fs, s.Journal, Options).CopyEntry(id);

        Assert.Equal(CopyOutcome.Done, result.Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
        Assert.Equal(File.GetLastWriteTimeUtc(s.Src), File.GetLastWriteTimeUtc(s.Dest));
        var entry = s.Journal.GetEntry(id);
        Assert.Equal(EntryState.Done, entry.State);
        Assert.NotNull(entry.SrcHash);
        Assert.Equal(entry.SrcHash, entry.DestHash);
        Assert.Empty(s.TempFiles());
        Assert.Empty(s.Journal.OpenAttempts());
    }

    [Fact]
    public void Copies_without_verification_when_no_hash_is_set()
    {
        using var s = new CopyScenario();
        s.WriteSource([1, 2, 3]);
        long id = s.AddEntry(hash: null);

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id).Outcome);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(s.Dest));
        Assert.Null(s.Journal.GetEntry(id).DestHash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk)]
    public void Copies_edge_sizes(int size)
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(size);
        s.WriteSource(content);
        long id = s.AddEntry();

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id).Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
    }

    [Fact]
    public void Copies_long_paths_with_unicode_names()
    {
        string deep = string.Join('\\', Enumerable.Repeat("a rather long folder name", 12));
        using var s = new CopyScenario(srcName: $@"src\{deep}\Café 写真 📷.NEF", destName: $@"dest\{deep}\Café 写真 📷.NEF");
        Assert.True(s.Dest.Length > 300);
        var content = TestDir.RandomBytes(Chunk + 5);
        s.WriteSource(content);
        long id = s.AddEntry();

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id).Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
    }

    [Fact]
    public void Replace_verifies_first_then_replaces_the_existing_file()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(Chunk + 1);
        s.WriteSource(content);
        s.WriteDest([7, 7, 7]);
        var seen = _fs.TryGetIdentity(s.Dest)!.Value;
        long id = s.AddEntry(action: ConflictAction.Replace, seenDest: seen);

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id).Outcome);

        Assert.Equal(content, File.ReadAllBytes(s.Dest));
        Assert.NotEqual(seen.FileId, _fs.TryGetIdentity(s.Dest)!.Value.FileId);
        var attempt = s.Journal.AttemptsFor(id).Single();
        Assert.Equal(seen.FileId, attempt.ReplacedFileId);
        Assert.Empty(s.TempFiles());
    }

    [Fact]
    public void Replace_keeps_a_destination_that_changed_after_the_decision()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(100));
        s.WriteDest([7, 7, 7]);
        long id = s.AddEntry(action: ConflictAction.Replace, seenDest: _fs.TryGetIdentity(s.Dest));
        Thread.Sleep(50);
        s.WriteDest([8, 8, 8, 8]); // someone edits it before we publish

        var result = new FileCopier(_fs, s.Journal, Options).CopyEntry(id);

        Assert.Equal(CopyOutcome.DestinationChanged, result.Outcome);
        Assert.Equal(new byte[] { 8, 8, 8, 8 }, File.ReadAllBytes(s.Dest));
        Assert.Equal(EntryState.Pending, s.Journal.GetEntry(id).State);
        Assert.Empty(s.TempFiles());
    }

    [Fact]
    public void Create_keeps_a_destination_that_appeared_after_planning()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(100));
        long id = s.AddEntry();
        s.WriteDest([9]);

        var result = new FileCopier(_fs, s.Journal, Options).CopyEntry(id);

        Assert.Equal(CopyOutcome.DestinationChanged, result.Outcome);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(s.Dest));
        Assert.Empty(s.TempFiles());
    }

    [Fact]
    public void Source_in_use_by_another_program_fails_with_reason()
    {
        using var s = new CopyScenario();
        s.WriteSource([1, 2, 3]);
        long id = s.AddEntry();
        using var writer = new FileStream(s.Src, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        var result = new FileCopier(_fs, s.Journal, Options).CopyEntry(id);

        Assert.Equal(CopyOutcome.Failed, result.Outcome);
        Assert.Equal("The source is in use by another program.", result.Message);
        Assert.False(File.Exists(s.Dest));
        Assert.Empty(s.TempFiles());
        Assert.Empty(s.Journal.AttemptsFor(id));
    }

    [Fact]
    public void Missing_source_fails_with_reason()
    {
        using var s = new CopyScenario();
        s.WriteSource([1]);
        long id = s.AddEntry();
        File.Delete(s.Src);

        var result = new FileCopier(_fs, s.Journal, Options).CopyEntry(id);

        Assert.Equal(CopyOutcome.Failed, result.Outcome);
        Assert.Equal("The source no longer exists.", result.Message);
    }

    [Fact]
    public void A_done_entry_is_not_copied_again()
    {
        using var s = new CopyScenario();
        s.WriteSource([1]);
        long id = s.AddEntry();
        var copier = new FileCopier(_fs, s.Journal, Options);
        copier.CopyEntry(id);

        Assert.Equal(CopyOutcome.Done, copier.CopyEntry(id).Outcome);
        Assert.Single(s.Journal.AttemptsFor(id));
    }

    [Fact]
    public void One_verification_failure_recopies_and_succeeds()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(Chunk * 2);
        s.WriteSource(content);
        long id = s.AddEntry();
        var faulty = new FaultyFileSystem(_fs) { CorruptVerifyReads = true };
        var copier = new FileCopier(faulty, s.Journal, Options);

        Assert.Equal(CopyOutcome.RetryNeeded, copier.CopyEntry(id).Outcome);
        Assert.Equal(ConflictAction.Replace, s.Journal.GetEntry(id).Action);

        faulty.CorruptVerifyReads = false;
        Assert.Equal(CopyOutcome.Done, copier.CopyEntry(id).Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
        Assert.Empty(s.TempFiles());
    }

    [Fact]
    public void Two_verification_failures_fail_the_entry_and_keep_the_source()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(Chunk));
        long id = s.AddEntry();
        var copier = new FileCopier(new FaultyFileSystem(_fs) { CorruptVerifyReads = true }, s.Journal, Options);

        Assert.Equal(CopyOutcome.RetryNeeded, copier.CopyEntry(id).Outcome);
        var second = copier.CopyEntry(id);

        Assert.Equal(CopyOutcome.Failed, second.Outcome);
        Assert.Contains("twice", second.Message);
        Assert.True(File.Exists(s.Src));
        Assert.Empty(s.TempFiles());
        Assert.Equal(EntryState.Failed, s.Journal.GetEntry(id).State);
    }

    [Fact]
    public void Rejects_unaligned_chunk_sizes() =>
        Assert.Throws<ArgumentException>(() => new FileCopier(_fs, null!, new CopyOptions { ChunkSize = 1000 }));
}
