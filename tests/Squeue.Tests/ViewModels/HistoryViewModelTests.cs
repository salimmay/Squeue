using Squeue.Core.Jobs;
using Squeue.Core.State;
using Squeue.ViewModels;

namespace Squeue.Tests.ViewModels;

public class HistoryViewModelTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 15, 0, 0, DateTimeKind.Local);

    private static MainViewModel NewMain(FakeJobQueue queue, Action<string>? openFolder = null) =>
        new(queue, new FakeDrives(), action => action(), new MemorySettings(), openFolder, () => Now);

    private static JobSnapshot Finished(long id = 1, int failed = 0, bool checkedCopies = false, DateTime? finished = null,
        JobState state = JobState.Done) =>
        FakeJobQueue.Snapshot(id, state, done: 100 * 1048576, failed: failed, finished: (finished ?? Now).ToUniversalTime(),
            checkedCopies: checkedCopies, created: (finished ?? Now).ToUniversalTime());

    [Fact]
    public void A_finished_job_appears_in_history()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);

        queue.Raise(Finished(checkedCopies: true));

        var item = Assert.Single(main.History);
        Assert.Equal("DCIM", item.Title);
        Assert.Equal(@"E:\DCIM → F:\Backup", item.Route);
        Assert.Equal("All checked", item.Result);
        Assert.Equal("Today", item.DayLabel);
        Assert.Equal("15:00 · 2 files · 100 MB", item.Details);
        Assert.False(item.NeedsAttention);

        queue.Raise(Finished(id: 2, checkedCopies: false));
        Assert.Equal("Copied, not checked", main.History.Single(h => h.Id == 2).Result);
    }

    [Fact]
    public void A_job_with_failures_needs_attention()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);

        queue.Raise(FakeJobQueue.Snapshot()); // running in this session, so the failed job keeps a card
        queue.Raise(Finished(failed: 2) with { LastError = "Access denied" });

        var item = Assert.Single(main.History);
        Assert.Equal("2 failed", item.Result);
        Assert.Equal("Access denied", item.Reason);
        Assert.True(item.NeedsAttention);
        Assert.True(item.CanRetry);
        Assert.Equal("History · 1 needs attention", main.HistoryHeader);
        Assert.Single(main.Jobs);
    }

    [Fact]
    public void Newest_jobs_come_first()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);

        queue.Raise(Finished(id: 1, finished: Now.AddHours(-3)));
        queue.Raise(Finished(id: 2, finished: Now.AddHours(-1)));
        queue.Raise(Finished(id: 3, finished: Now.AddHours(-2)));

        Assert.Equal([2L, 3L, 1L], main.History.Select(h => h.Id));
    }

    [Fact]
    public void Yesterday_and_older_days_are_labelled()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);

        queue.Raise(Finished(id: 1, finished: Now.AddDays(-1)));
        queue.Raise(Finished(id: 2, finished: new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Local)));

        Assert.Equal("Yesterday", main.History.Single(h => h.Id == 1).DayLabel);
        Assert.Equal("12 Sep 2026", main.History.Single(h => h.Id == 2).DayLabel);
    }

    [Fact]
    public void Retry_failed_goes_to_the_queue_and_the_item_leaves_history()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);
        queue.Raise(Finished(id: 7, failed: 1));

        main.History[0].RetryFailedCommand.Execute(null);
        Assert.Contains("RetryFailed 7", queue.Calls);

        queue.Raise(FakeJobQueue.Snapshot(7, JobState.Queued));
        Assert.Empty(main.History);
        Assert.Equal("History", main.HistoryHeader);
    }

    [Fact]
    public void Failed_files_are_loaded_on_first_open()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);
        queue.Raise(Finished(id: 7, failed: 1));
        var item = main.History[0];

        item.ToggleFailedFilesCommand.Execute(null);
        Assert.True(item.ShowsFailedFiles);
        Assert.Single(queue.Calls, "RequestFailedFiles 7");

        queue.RaiseFailedFiles(7, [new FailedFile(@"E:\DCIM\a.jpg", "Access denied")]);
        Assert.Equal(@"E:\DCIM\a.jpg", Assert.Single(item.FailedFiles).Path);

        item.ToggleFailedFilesCommand.Execute(null);
        Assert.False(item.ShowsFailedFiles);
        Assert.Single(queue.Calls, "RequestFailedFiles 7");
    }

    [Fact]
    public void Open_folder_passes_the_destination()
    {
        var queue = new FakeJobQueue();
        string? opened = null;
        var main = NewMain(queue, folder => opened = folder);
        queue.Raise(Finished());

        main.History[0].OpenFolderCommand.Execute(null);

        Assert.Equal(@"F:\Backup", opened);
    }

    [Fact]
    public void A_cancelled_job_is_listed_as_cancelled()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);

        queue.Raise(Finished(state: JobState.Cancelled));

        var item = Assert.Single(main.History);
        Assert.Equal("Cancelled", item.Result);
        Assert.False(item.CanRetry);
    }

    [Fact]
    public void Retrying_asks_to_show_the_queue()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);
        int asked = 0;
        main.ShowQueueRequested += () => asked++;
        queue.Raise(Finished(id: 7, failed: 1));

        main.History[0].RetryFailedCommand.Execute(null);

        Assert.Equal(1, asked);
    }

    [Fact]
    public void The_failed_files_note_follows_the_answer()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);
        queue.Raise(Finished(id: 7, failed: 1));
        var item = main.History[0];
        var changes = new List<string?>();
        item.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.Equal("Loading…", item.FailedFilesNote);

        queue.RaiseFailedFiles(7, []);
        Assert.Equal("No failed files were recorded.", item.FailedFilesNote);
        Assert.Contains(nameof(HistoryItemViewModel.FailedFilesNote), changes);

        queue.RaiseFailedFiles(7, [new FailedFile(@"E:.jpg", "Access denied")]);
        Assert.Equal("", item.FailedFilesNote);

        queue.RaiseFailedFiles(7, Enumerable.Range(0, 500).Select(i => new FailedFile($@"E:\{i}.jpg", "x")).ToList());
        Assert.Equal("Showing the first 500.", item.FailedFilesNote);
    }
}
