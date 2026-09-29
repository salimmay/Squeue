using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Copy;

public class DeferredVerificationTests
{
    private const int Chunk = 64 * 1024;
    private static readonly CopyOptions Options = new() { ChunkSize = Chunk, SharingRetryTimeout = TimeSpan.Zero };
    private readonly WindowsFileSystem _fs = new();

    [Fact]
    public void A_deferred_copy_is_published_now_and_verified_later()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(3 * Chunk + 1);
        s.WriteSource(content);
        long id = s.AddEntry();
        var copier = new FileCopier(_fs, s.Journal, Options);

        var first = copier.CopyEntry(id, deferVerification: true);

        Assert.Equal(CopyOutcome.Published, first.Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
        Assert.Equal(EntryState.Active, s.Journal.GetEntry(id).State);
        Assert.Equal(AttemptPhase.Published, s.Journal.GetOpenAttempt(id)!.Phase);
        Assert.Null(s.Journal.GetEntry(id).DestHash);

        var second = copier.CopyEntry(id);

        Assert.Equal(CopyOutcome.Done, second.Outcome);
        Assert.Equal(s.Journal.GetEntry(id).SrcHash, s.Journal.GetEntry(id).DestHash);
        Assert.Single(s.Journal.AttemptsFor(id));
    }

    [Fact]
    public void Resuming_a_published_copy_with_deferral_leaves_it_published()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(1000));
        long id = s.AddEntry();
        var copier = new FileCopier(_fs, s.Journal, Options);
        copier.CopyEntry(id, deferVerification: true);

        Assert.Equal(CopyOutcome.Published, copier.CopyEntry(id, deferVerification: true).Outcome);
        Assert.Equal(AttemptPhase.Published, s.Journal.GetOpenAttempt(id)!.Phase);
    }

    [Fact]
    public void Without_a_hash_a_deferred_copy_finishes_at_once()
    {
        using var s = new CopyScenario();
        s.WriteSource([1, 2, 3]);
        long id = s.AddEntry(hash: null);

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id, deferVerification: true).Outcome);
    }

    [Fact]
    public void A_replacement_is_verified_before_publishing_even_when_deferred()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(1000));
        s.WriteDest([9]);
        long id = s.AddEntry(action: ConflictAction.Replace, seenDest: _fs.TryGetIdentity(s.Dest));

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id, deferVerification: true).Outcome);
    }

    [Fact]
    public void A_failed_deferred_verification_asks_for_a_new_copy()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(2 * Chunk));
        long id = s.AddEntry();
        var faulty = new FaultyFileSystem(_fs) { CorruptVerifyReads = true };
        var copier = new FileCopier(faulty, s.Journal, Options);
        Assert.Equal(CopyOutcome.Published, copier.CopyEntry(id, deferVerification: true).Outcome);

        var result = copier.CopyEntry(id);

        Assert.Equal(CopyOutcome.RetryNeeded, result.Outcome);
        Assert.Equal(EntryState.Pending, s.Journal.GetEntry(id).State);
        Assert.Equal(ConflictAction.Replace, s.Journal.GetEntry(id).Action);
    }
}
