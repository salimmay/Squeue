using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Copy;

public class ReconcilerTests
{
    private static readonly CopyOptions Options = new() { ChunkSize = 64 * 1024, SharingRetryTimeout = TimeSpan.Zero };
    private readonly WindowsFileSystem _fs = new();

    private long CrashDuringCopy(CopyScenario s, FsOp op, bool after, int occurrence = 1)
    {
        long id = s.AddEntry();
        var faulty = new FaultyFileSystem(_fs) { CrashAt = (op, after, occurrence) };
        Assert.Throws<SimulatedCrashException>(() => new FileCopier(faulty, s.Journal, Options).CopyEntry(id));
        s.ReopenJournal();
        return id;
    }

    [Fact]
    public void Temp_created_but_not_yet_journaled_is_removed()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(1000));
        long id = CrashDuringCopy(s, FsOp.CreateTemp, after: true);
        Assert.Single(s.TempFiles());

        var report = new Reconciler(_fs, s.Journal).Run();

        Assert.Equal(new[] { id }, report.Reset);
        Assert.Empty(s.TempFiles());
        Assert.Equal(EntryState.Pending, s.Journal.GetEntry(id).State);
        Assert.Empty(s.Journal.OpenAttempts());
    }

    [Fact]
    public void Half_written_temp_is_removed_and_the_copy_restarts()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(200_000);
        s.WriteSource(content);
        long id = CrashDuringCopy(s, FsOp.Write, after: true, occurrence: 2);

        var report = new Reconciler(_fs, s.Journal).Run();
        Assert.Equal(new[] { id }, report.Reset);
        Assert.Empty(s.TempFiles());

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id).Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
    }

    [Fact]
    public void Rename_that_happened_before_the_crash_is_recognised_and_resumed()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(5000);
        s.WriteSource(content);
        long id = CrashDuringCopy(s, FsOp.Rename, after: true);
        Assert.True(File.Exists(s.Dest));

        var report = new Reconciler(_fs, s.Journal).Run();

        Assert.Equal(new[] { id }, report.Resumed);
        Assert.Equal(AttemptPhase.Published, s.Journal.GetOpenAttempt(id)!.Phase);
        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id).Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
        Assert.Single(s.Journal.AttemptsFor(id)); // not copied twice
    }

    [Fact]
    public void A_file_at_our_temp_name_with_a_different_id_is_left_alone()
    {
        using var s = new CopyScenario();
        s.WriteSource([1]);
        long id = s.AddEntry();
        s.WriteDest([0]);
        string foreign = Path.Combine(s.DestDir, "~tqaaaaaaaaaa.tmp");
        File.WriteAllBytes(foreign, [42]);
        long attempt = s.Journal.BeginAttempt(id, "~tqaaaaaaaaaa.tmp", null);
        s.Journal.SetPhase(attempt, AttemptPhase.TempCreated, tempFileId: (UInt128)12345);

        var report = new Reconciler(_fs, s.Journal).Run();

        Assert.Equal(new[] { foreign }, report.Unowned);
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(foreign));
        Assert.Equal(EntryState.Pending, s.Journal.GetEntry(id).State);
    }

    [Fact]
    public void An_older_file_at_our_temp_name_with_no_journaled_id_is_left_alone()
    {
        using var s = new CopyScenario();
        s.WriteSource([1]);
        long id = s.AddEntry();
        Directory.CreateDirectory(s.DestDir);
        string foreign = Path.Combine(s.DestDir, "~tqbbbbbbbbbb.tmp");
        File.WriteAllBytes(foreign, [42]);
        File.SetCreationTimeUtc(foreign, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        s.Journal.BeginAttempt(id, "~tqbbbbbbbbbb.tmp", null);

        var report = new Reconciler(_fs, s.Journal).Run();

        Assert.Equal(new[] { foreign }, report.Unowned);
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void Published_file_replaced_by_someone_else_fails_the_entry_and_keeps_both()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(5000));
        long id = CrashDuringCopy(s, FsOp.OpenForVerify, after: false); // crash right after publishing
        File.Delete(s.Dest);
        File.WriteAllBytes(s.Dest, [5, 5]);

        var report = new Reconciler(_fs, s.Journal).Run();

        Assert.Equal(new[] { id }, report.Failed);
        Assert.Equal(new byte[] { 5, 5 }, File.ReadAllBytes(s.Dest));
        Assert.True(File.Exists(s.Src));
        Assert.Equal(EntryState.Failed, s.Journal.GetEntry(id).State);
    }

    [Fact]
    public void An_attempt_on_a_missing_drive_is_left_alone()
    {
        using var s = new CopyScenario();
        s.WriteSource([1, 2, 3]);
        string dest = TestDir.MissingDriveRoot() + @"Backup\a.bin";
        long id = s.Journal.AddEntry(s.Journal.CreateJob("xxh3"), s.Src, dest, 3, ConflictAction.Create, null);
        s.Journal.SetEntryState(id, EntryState.Active);
        long attempt = s.Journal.BeginAttempt(id, "~tqcccccccccc.tmp", null);
        s.Journal.SetPhase(attempt, AttemptPhase.TempCreated, tempFileId: (UInt128)7);
        s.Journal.SetPhase(attempt, AttemptPhase.Published, publishedFileId: (UInt128)7);

        var report = new Reconciler(_fs, s.Journal).Run();

        Assert.Equal(new[] { id }, report.Deferred);
        Assert.Empty(report.Failed);
        Assert.Empty(report.Reset);
        Assert.Equal(AttemptPhase.Published, s.Journal.GetOpenAttempt(id)!.Phase);
        Assert.Equal(EntryState.Active, s.Journal.GetEntry(id).State);
    }

    [Fact]
    public void Running_twice_changes_nothing_the_second_time()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(1000));
        CrashDuringCopy(s, FsOp.Flush, after: true);
        new Reconciler(_fs, s.Journal).Run();
        var filesAfterFirst = Directory.GetFiles(s.Dir.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var second = new Reconciler(_fs, s.Journal).Run();

        Assert.Empty(second.Reset);
        Assert.Empty(second.Resumed);
        Assert.Empty(second.Failed);
        Assert.Empty(second.Unowned);
        Assert.Equal(filesAfterFirst, Directory.GetFiles(s.Dir.Path, "*", SearchOption.AllDirectories).Order().ToArray());
    }
}
