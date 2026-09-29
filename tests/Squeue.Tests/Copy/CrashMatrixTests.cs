using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Copy;

public class CrashMatrixTests
{
    private const int Chunk = 64 * 1024;
    private static readonly CopyOptions Options = new() { ChunkSize = Chunk, SharingRetryTimeout = TimeSpan.Zero };

    public static TheoryData<bool, FsOp, bool, int> CrashPoints()
    {
        var data = new TheoryData<bool, FsOp, bool, int>();
        foreach (bool replace in new[] { false, true })
        foreach (FsOp op in Enum.GetValues<FsOp>())
        foreach (bool after in new[] { false, true })
        {
            data.Add(replace, op, after, 1);
            if (op is FsOp.Write or FsOp.Flush or FsOp.VerifyRead or FsOp.TryGetIdentity) data.Add(replace, op, after, 2);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public void Recovery_after_a_crash_leaves_one_valid_copy_and_touches_nothing_else(bool replace, FsOp op, bool after, int occurrence)
    {
        using var s = new CopyScenario();
        var real = new WindowsFileSystem();
        var content = TestDir.RandomBytes(3 * Chunk + 123, seed: 7);
        var original = TestDir.RandomBytes(5000, seed: 9);
        s.WriteSource(content);

        Directory.CreateDirectory(s.DestDir);
        string decoy = Path.Combine(s.DestDir, "~tqzzzzzzzzzz.tmp"); // looks like ours, isn't
        string unrelated = Path.Combine(s.DestDir, "unrelated.txt");
        File.WriteAllBytes(decoy, [42]);
        File.WriteAllBytes(unrelated, [1, 2, 3]);

        long id;
        if (replace)
        {
            s.WriteDest(original);
            id = s.AddEntry(action: ConflictAction.Replace, seenDest: real.TryGetIdentity(s.Dest));
        }
        else
        {
            id = s.AddEntry();
        }

        var faulty = new FaultyFileSystem(real) { CrashAt = (op, after, occurrence) };
        try { new FileCopier(faulty, s.Journal, Options).CopyEntry(id); }
        catch (SimulatedCrashException) { }

        // I2: at the moment of the crash the final name holds the old file or the complete new one, nothing else.
        AssertOriginalOrNew(s.Dest, replace ? original : null, content);

        // Restart: new database connection, real file system, reconcile, then finish the job.
        s.ReopenJournal();
        new Reconciler(real, s.Journal).Run();
        var copier = new FileCopier(real, s.Journal, Options);
        var result = copier.CopyEntry(id);
        if (result.Outcome == CopyOutcome.RetryNeeded) result = copier.CopyEntry(id);

        Assert.Equal(CopyOutcome.Done, result.Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
        Assert.Empty(s.Journal.OpenAttempts());

        // I4: only our own temp files were removed.
        Assert.Equal(new[] { decoy }, s.TempFiles());
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(decoy));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(unrelated));
        Assert.Equal(content, File.ReadAllBytes(s.Src));

        // I3: reconciling again finds nothing to do.
        var again = new Reconciler(real, s.Journal).Run();
        Assert.Empty(again.Reset);
        Assert.Empty(again.Failed);
    }

    public static TheoryData<FsOp, bool> VerifyFailureCrashPoints() => new()
    {
        { FsOp.TryGetIdentity, false }, { FsOp.TryGetIdentity, true },
        { FsOp.Delete, false }, { FsOp.Delete, true },
    };

    [Theory]
    [MemberData(nameof(VerifyFailureCrashPoints))]
    public void Recovery_after_a_crash_while_handling_a_verification_failure(FsOp op, bool after)
    {
        using var s = new CopyScenario();
        var real = new WindowsFileSystem();
        var content = TestDir.RandomBytes(3 * Chunk + 123, seed: 7);
        s.WriteSource(content);

        Directory.CreateDirectory(s.DestDir);
        string decoy = Path.Combine(s.DestDir, "~tqzzzzzzzzzz.tmp");
        string unrelated = Path.Combine(s.DestDir, "unrelated.txt");
        File.WriteAllBytes(decoy, [42]);
        File.WriteAllBytes(unrelated, [1, 2, 3]);

        long id = s.AddEntry();

        var faulty = new FaultyFileSystem(real) { CorruptVerifyReads = true, CrashAt = (op, after, 1) };
        var faultyCopier = new FileCopier(faulty, s.Journal, Options);
        for (int i = 0; i < 3; i++)
        {
            try { faultyCopier.CopyEntry(id); }
            catch (SimulatedCrashException) { break; }
        }
        Assert.True(faulty.Crashed, "The crash point was never reached.");

        s.ReopenJournal();
        new Reconciler(real, s.Journal).Run();
        var copier = new FileCopier(real, s.Journal, Options);
        var result = copier.CopyEntry(id);
        for (int i = 0; i < 2 && result.Outcome == CopyOutcome.RetryNeeded; i++) result = copier.CopyEntry(id);

        Assert.Equal(CopyOutcome.Done, result.Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
        Assert.Empty(s.Journal.OpenAttempts());

        Assert.Equal(new[] { decoy }, s.TempFiles());
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(decoy));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(unrelated));
        Assert.Equal(content, File.ReadAllBytes(s.Src));

        var again = new Reconciler(real, s.Journal).Run();
        Assert.Empty(again.Reset);
        Assert.Empty(again.Failed);
    }

    private static void AssertOriginalOrNew(string dest, byte[]? original, byte[] content)
    {
        if (!File.Exists(dest))
        {
            Assert.Null(original); // a replacement must never leave the destination missing
            return;
        }
        var actual = File.ReadAllBytes(dest);
        Assert.True(actual.AsSpan().SequenceEqual(content) || (original is not null && actual.AsSpan().SequenceEqual(original)),
            "The destination held a partial or unexpected file after the crash.");
    }
}
