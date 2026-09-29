using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Squeue.Core.Jobs;
using Squeue.Core.State;

namespace Squeue.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IJobQueue _queue;
    private readonly IDriveSource _drives;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<long, JobCardViewModel> _cards = [];
    private readonly Dictionary<long, JobSnapshot> _latest = [];

    /// <param name="post">Runs an action on the UI thread; the queue raises events on its own thread.</param>
    public MainViewModel(IJobQueue queue, IDriveSource drives, Action<Action> post, Func<DateTime>? now = null)
    {
        _queue = queue;
        _drives = drives;
        _now = now ?? (() => DateTime.Now);
        queue.JobChanged += snapshot => post(() => Apply(snapshot));
        RefreshDrives();
        UpdateSummary();
    }

    public ObservableCollection<JobCardViewModel> Jobs { get; } = [];
    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _doneToday = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    private PlanViewModel? _pendingPlan;

    public bool HasPlan => PendingPlan is not null;

    public void Apply(JobSnapshot snapshot)
    {
        _latest[snapshot.Id] = snapshot;
        if (IsActive(snapshot))
        {
            if (!_cards.TryGetValue(snapshot.Id, out var card))
            {
                card = new JobCardViewModel(snapshot.Id, _queue);
                _cards[snapshot.Id] = card;
                Jobs.Add(card);
            }
            card.Update(snapshot);
        }
        else if (_cards.Remove(snapshot.Id, out var finished))
        {
            Jobs.Remove(finished);
        }
        UpdateSummary();
        UpdateBusyDrives();
    }

    public void ProposePlan(JobPlan plan) => PendingPlan = new PlanViewModel(plan, StartPlan, _ => PendingPlan = null);

    [RelayCommand]
    public void RefreshDrives()
    {
        Drives.Clear();
        foreach (var drive in _drives.ReadyDrives()) Drives.Add(new DriveViewModel(drive));
        UpdateBusyDrives();
    }

    private void StartPlan(PlanViewModel plan)
    {
        _queue.Enqueue(plan.Plan, plan.Policy, plan.Verify);
        PendingPlan = null;
    }

    private void UpdateSummary()
    {
        var active = _latest.Values.Where(IsActive).ToList();
        long left = active.Sum(s => s.TotalBytes - s.DoneBytes);
        double speed = active.Where(s => s.State == JobState.Running).Sum(s => s.BytesPerSecond);
        Subtitle = active.Count == 0
            ? "Nothing to copy. Drop files or folders here."
            : speed > 0
                ? $"{Format.Bytes(left)} left · {Format.TimeLeft(TimeSpan.FromSeconds(left / speed))}"
                : $"{Format.Bytes(left)} left";

        int doneToday = _latest.Values.Count(s => s.State == JobState.Done && s.CreatedAtUtc.ToLocalTime().Date == _now().Date);
        DoneToday = doneToday == 0 ? "" : $"{doneToday} done today";
    }

    private void UpdateBusyDrives()
    {
        var busyRoots = _latest.Values
            .Where(s => s.State == JobState.Running)
            .SelectMany(s => new[] { Path.GetPathRoot(s.Source), Path.GetPathRoot(s.DestRoot) })
            .Where(root => !string.IsNullOrEmpty(root))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in Drives) drive.IsBusy = busyRoots.Contains(drive.Root);
    }

    private static bool IsActive(JobSnapshot s) => s.State is JobState.Queued or JobState.Running or JobState.Paused;
}
