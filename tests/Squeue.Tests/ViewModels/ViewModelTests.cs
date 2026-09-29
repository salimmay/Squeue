using Squeue.Core.Jobs;
using Squeue.Core.State;
using Squeue.ViewModels;

namespace Squeue.Tests.ViewModels;

public class ViewModelTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 15, 0, 0, DateTimeKind.Local);

    private static MainViewModel NewMain(FakeJobQueue queue, params DriveInfoLite[] drives) =>
        new(queue, new FakeDrives(drives), action => action(), () => Now);

    [Fact]
    public void A_running_job_shows_a_card_with_progress_speed_and_time_left()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);

        queue.Raise(FakeJobQueue.Snapshot());

        var card = Assert.Single(main.Jobs);
        Assert.Equal(("DCIM", @"E:\DCIM → F:\Backup", "25%"), (card.Title, card.Route, card.Percent));
        Assert.Equal(("Copying · 10 MB/s", "under a minute left"), (card.Status, card.TimeLeft));
        Assert.Equal((true, true, false), (card.IsRunning, card.CanPause, card.CanResume));
        Assert.Equal("75 MB left · under a minute left", main.Subtitle);
    }

    [Fact]
    public void A_queued_job_says_it_is_waiting()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);

        queue.Raise(FakeJobQueue.Snapshot(state: JobState.Queued, done: 0, speed: 0));

        Assert.Equal("Waiting · 2 files, 100 MB", main.Jobs[0].Status);
        Assert.Equal("100 MB left", main.Subtitle);
    }

    [Fact]
    public void A_finished_job_leaves_the_list_and_counts_as_done_today()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);
        queue.Raise(FakeJobQueue.Snapshot());

        queue.Raise(FakeJobQueue.Snapshot(state: JobState.Done, done: 100 * 1048576, created: Now.ToUniversalTime()));

        Assert.Empty(main.Jobs);
        Assert.Equal("1 done today", main.DoneToday);
        Assert.Equal("Nothing to copy. Drop files or folders here.", main.Subtitle);
    }

    [Fact]
    public void Card_buttons_go_to_the_queue()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);
        queue.Raise(FakeJobQueue.Snapshot(id: 7));

        main.Jobs[0].PauseCommand.Execute(null);
        main.Jobs[0].ResumeCommand.Execute(null);
        main.Jobs[0].CancelCommand.Execute(null);

        Assert.Equal(new[] { "Pause 7", "Resume 7", "Cancel 7" }, queue.Calls);
    }

    [Fact]
    public void Starting_a_plan_enqueues_it_and_closes_the_panel()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);
        var plan = new JobPlan("DCIM", @"E:\DCIM", @"F:\Backup", [new PlannedFile(@"E:\DCIM\a", @"F:\Backup\DCIM\a", 10, null)]);

        main.ProposePlan(plan);
        Assert.True(main.HasPlan);
        main.PendingPlan!.StartCommand.Execute(null);

        Assert.Equal(new[] { "Enqueue DCIM Skip True" }, queue.Calls);
        Assert.False(main.HasPlan);
    }

    [Fact]
    public void The_plan_summary_mentions_existing_files()
    {
        var existing = new Squeue.Core.FileSystem.FileIdentity(1, (UInt128)2, 3, 4, 5, 6, 0);
        var plan = new JobPlan("DCIM", @"E:\DCIM", @"F:\Backup",
        [
            new PlannedFile(@"E:\DCIM\a", @"F:\Backup\DCIM\a", 1048576, existing),
            new PlannedFile(@"E:\DCIM\b", @"F:\Backup\DCIM\b", 1048576, null),
        ]);

        var vm = new PlanViewModel(plan, _ => { }, _ => { });

        Assert.Equal(("Copy DCIM", @"2 files · 2 MB to F:\Backup"), (vm.Title, vm.Summary));
        Assert.Equal((true, "1 file already exists at the destination"), (vm.HasExisting, vm.ExistingNote));
        vm.Overwrite = true;
        Assert.Equal(OverwritePolicy.Replace, vm.Policy);
    }

    [Fact]
    public void The_plan_summary_warns_about_unreadable_folders()
    {
        var file = new PlannedFile(@"E:\DCIM\a", @"F:\Backup\DCIM\a", 10, null);
        var skipped = new JobPlan("DCIM", @"E:\DCIM", @"F:\Backup", [file])
        {
            SkippedFolders = [@"E:\DCIM\locked", @"E:\DCIM\other"],
        };
        var clean = new JobPlan("DCIM", @"E:\DCIM", @"F:\Backup", [file]);

        var withSkipped = new PlanViewModel(skipped, _ => { }, _ => { });
        var without = new PlanViewModel(clean, _ => { }, _ => { });

        Assert.Equal((true, "2 folders couldn't be read and will be skipped"), (withSkipped.HasSkipped, withSkipped.SkippedNote));
        Assert.Equal((false, (string?)null), (without.HasSkipped, without.SkippedNote));
    }

    [Fact]
    public void A_job_paused_by_an_error_says_why()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);

        queue.Raise(FakeJobQueue.Snapshot(state: JobState.Paused) with { LastError = "The device is not ready." });

        Assert.Equal("Paused · The device is not ready.", main.Jobs[0].Status);
    }

    [Fact]
    public void A_nearly_finished_running_job_never_shows_100_percent()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);

        queue.Raise(FakeJobQueue.Snapshot(total: 1000, done: 999));

        Assert.Equal("99%", main.Jobs[0].Percent);
    }

    [Fact]
    public void An_empty_running_job_shows_no_time_left()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);

        queue.Raise(FakeJobQueue.Snapshot(total: 0, done: 0, speed: 0));

        Assert.Equal("", main.Jobs[0].TimeLeft);
    }

    [Fact]
    public void HasPlan_and_Policy_raise_change_notifications()
    {
        var main = NewMain(new FakeJobQueue());
        var mainRaised = new List<string?>();
        main.PropertyChanged += (_, e) => mainRaised.Add(e.PropertyName);

        main.ProposePlan(new JobPlan("DCIM", @"E:\DCIM", @"F:\Backup", []));
        var planRaised = new List<string?>();
        main.PendingPlan!.PropertyChanged += (_, e) => planRaised.Add(e.PropertyName);
        main.PendingPlan.Overwrite = true;

        Assert.Contains("HasPlan", mainRaised);
        Assert.Contains("Policy", planRaised);
    }

    [Fact]
    public void Queue_events_are_applied_through_post()
    {
        var queue = new FakeJobQueue();
        var posted = new List<Action>();
        var main = new MainViewModel(queue, new FakeDrives(), posted.Add, () => Now);

        queue.Raise(FakeJobQueue.Snapshot());
        Assert.Empty(main.Jobs);
        foreach (var action in posted.ToList()) action();

        Assert.Single(main.Jobs);
    }

    [Fact]
    public void Drives_used_by_a_running_job_are_busy()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue, new(@"C:\", "C:"), new(@"E:\", "EOS_DIGITAL (E:)"), new(@"F:\", "Backup (F:)"));

        queue.Raise(FakeJobQueue.Snapshot());

        Assert.Equal(new[] { false, true, true }, main.Drives.Select(d => d.IsBusy));
        Assert.Equal("EOS_DIGITAL (E:)", main.Drives[1].Name);
    }
}
