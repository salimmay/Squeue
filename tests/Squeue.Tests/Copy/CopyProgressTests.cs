using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Tests.Copy;

public class CopyProgressTests
{
    private const int Chunk = 64 * 1024;
    private static readonly CopyOptions Options = new() { ChunkSize = Chunk, SharingRetryTimeout = TimeSpan.Zero };
    private readonly WindowsFileSystem _fs = new();

    private sealed class Recorder(Action<CopyProgress>? onReport = null) : IProgress<CopyProgress>
    {
        public List<CopyProgress> Reports { get; } = [];

        public void Report(CopyProgress value)
        {
            Reports.Add(value);
            onReport?.Invoke(value);
        }
    }

    [Fact]
    public void Reports_copy_then_verify_progress_up_to_the_file_size()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(3 * Chunk + 1);
        s.WriteSource(content);
        long id = s.AddEntry();
        var recorder = new Recorder();

        var result = new FileCopier(_fs, s.Journal, Options).CopyEntry(id, recorder);

        Assert.Equal(CopyOutcome.Done, result.Outcome);
        var copying = recorder.Reports.Where(r => r.Stage == CopyStage.Copying).ToList();
        var verifying = recorder.Reports.Where(r => r.Stage == CopyStage.Verifying).ToList();
        Assert.Equal(4, copying.Count);
        Assert.Equal(content.Length, copying[^1].BytesDone);
        Assert.All(recorder.Reports, r => Assert.Equal(content.Length, r.BytesTotal));
        Assert.Equal(content.Length, verifying[^1].BytesDone);
        Assert.True(recorder.Reports.IndexOf(copying[^1]) < recorder.Reports.IndexOf(verifying[0]));
    }

    [Fact]
    public void Cancelling_while_writing_removes_the_temp_and_leaves_the_entry_pending()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(4 * Chunk);
        s.WriteSource(content);
        long id = s.AddEntry();
        using var stop = new CancellationTokenSource();
        var copier = new FileCopier(_fs, s.Journal, Options);

        var result = copier.CopyEntry(id, new Recorder(_ => stop.Cancel()), stop.Token);

        Assert.Equal(CopyOutcome.Cancelled, result.Outcome);
        Assert.False(File.Exists(s.Dest));
        Assert.Empty(s.TempFiles());
        Assert.Equal(EntryState.Pending, s.Journal.GetEntry(id).State);
        Assert.Empty(s.Journal.OpenAttempts());

        Assert.Equal(CopyOutcome.Done, copier.CopyEntry(id).Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
    }

    [Fact]
    public void Cancelling_while_verifying_a_published_copy_finishes_it_next_time()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(2 * Chunk);
        s.WriteSource(content);
        long id = s.AddEntry();
        using var stop = new CancellationTokenSource();
        var copier = new FileCopier(_fs, s.Journal, Options);

        var result = copier.CopyEntry(id, new Recorder(p => { if (p.Stage == CopyStage.Verifying) stop.Cancel(); }), stop.Token);

        Assert.Equal(CopyOutcome.Cancelled, result.Outcome);
        Assert.Equal(AttemptPhase.Published, s.Journal.GetOpenAttempt(id)!.Phase);
        Assert.Equal(CopyOutcome.Done, copier.CopyEntry(id).Outcome);
        Assert.Single(s.Journal.AttemptsFor(id));
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
    }

    [Fact]
    public void Cancelling_while_checking_a_replacement_keeps_the_existing_file()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(2 * Chunk));
        s.WriteDest([7, 7, 7]);
        long id = s.AddEntry(action: ConflictAction.Replace, seenDest: _fs.TryGetIdentity(s.Dest));
        using var stop = new CancellationTokenSource();
        var copier = new FileCopier(_fs, s.Journal, Options);

        var result = copier.CopyEntry(id, new Recorder(p => { if (p.Stage == CopyStage.Verifying) stop.Cancel(); }), stop.Token);

        Assert.Equal(CopyOutcome.Cancelled, result.Outcome);
        Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(s.Dest));
        Assert.Empty(s.TempFiles());
        Assert.Equal(EntryState.Pending, s.Journal.GetEntry(id).State);
        Assert.Empty(s.Journal.OpenAttempts());
    }

    [Fact]
    public void An_already_cancelled_token_changes_nothing()
    {
        using var s = new CopyScenario();
        s.WriteSource([1, 2, 3]);
        long id = s.AddEntry();
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var result = new FileCopier(_fs, s.Journal, Options).CopyEntry(id, null, stop.Token);

        Assert.Equal(CopyOutcome.Cancelled, result.Outcome);
        Assert.Empty(s.Journal.AttemptsFor(id));
        Assert.Equal(EntryState.Pending, s.Journal.GetEntry(id).State);
    }
}
