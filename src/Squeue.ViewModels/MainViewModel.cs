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
    private readonly Action<string>? _openFolder;
    private readonly Dictionary<long, HistoryItemViewModel> _history = [];
    private int _activeCount;
    private readonly Dictionary<long, JobCardViewModel> _cards = [];
    private readonly Dictionary<long, JobSnapshot> _latest = [];
    private readonly HashSet<long> _dismissed = []; // cards the user closed; later snapshots don't bring them back

    /// <param name="post">Runs an action on the UI thread; the queue raises events on its own thread.</param>
    public MainViewModel(IJobQueue queue, IDriveSource drives, Action<Action> post, ISettingsStore settings,
        Action<string>? openFolder = null, Func<DateTime>? now = null)
    {
        _queue = queue;
        _drives = drives;
        _openFolder = openFolder;
        _now = now ?? (() => DateTime.Now);
        Settings = new SettingsViewModel(settings);
        Jobs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        queue.JobChanged += snapshot => post(() => Apply(snapshot));
        queue.FailedFilesLoaded += (id, files) => post(() =>
        {
            if (_history.TryGetValue(id, out var item)) item.SetFailedFiles(files);
        });
        RefreshDrives();
        UpdateSummary();
    }

    public SettingsViewModel Settings { get; }
    public ObservableCollection<JobCardViewModel> Jobs { get; } = [];
    public ObservableCollection<HistoryItemViewModel> History { get; } = [];

    /// Raised when the last active job ends.
    public event Action? QueueFinished;

    /// No cards and no pending plan: the queue shows its empty state.
    public bool IsEmpty => Jobs.Count == 0 && PendingPlan is null;

    [ObservableProperty] private string _historyHeader = "History";
    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _doneToday = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private PlanViewModel? _pendingPlan;

    public bool HasPlan => PendingPlan is not null;

    public void Apply(JobSnapshot snapshot)
    {
        _latest[snapshot.Id] = snapshot;
        if (NeedsCard(snapshot) && !_dismissed.Contains(snapshot.Id))
        {
            if (!_cards.TryGetValue(snapshot.Id, out var card))
            {
                card = new JobCardViewModel(snapshot.Id, _queue, Dismiss);
                _cards[snapshot.Id] = card;
                Jobs.Add(card);
            }
            card.Update(snapshot);
        }
        else if (_cards.Remove(snapshot.Id, out var finished))
        {
            Jobs.Remove(finished);
        }
        UpdateHistory(snapshot);
        UpdateSummary();
        UpdateBusyDrives();
    }

    private void Dismiss(JobCardViewModel card)
    {
        _dismissed.Add(card.Id);
        if (_cards.Remove(card.Id)) Jobs.Remove(card);
    }

    public void ProposePlan(JobPlan plan) => PendingPlan = new PlanViewModel(plan, StartPlan, _ => PendingPlan = null)
    {
        Verify = Settings.CheckAfterCopying,
        Overwrite = Settings.ExistingFiles == ExistingFiles.Replace, // replacing forces Verify on
    };

    private void UpdateHistory(JobSnapshot snapshot)
    {
        if (snapshot.State is JobState.Done or JobState.Cancelled)
        {
            if (!_history.TryGetValue(snapshot.Id, out var item))
            {
                item = new HistoryItemViewModel(snapshot.Id, _queue, _openFolder, _now);
                _history[snapshot.Id] = item;
                item.Update(snapshot);
                int index = 0;
                while (index < History.Count && History[index].FinishedLocal >= item.FinishedLocal) index++;
                History.Insert(index, item);
            }
            else
            {
                item.Update(snapshot);
            }
        }
        else if (_history.Remove(snapshot.Id, out var gone))
        {
            History.Remove(gone);
        }
        int attention = History.Count(h => h.NeedsAttention);
        HistoryHeader = attention == 0 ? "History"
            : attention == 1 ? "History · 1 needs attention"
            : $"History · {attention} need attention";
    }

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
        double speed = active.Where(s => s.State is JobState.Running or JobState.Verifying).Sum(s => s.BytesPerSecond);
        if (_activeCount > 0 && active.Count == 0) QueueFinished?.Invoke();
        _activeCount = active.Count;
        string eta = Format.TimeLeftFor(left, speed);
        Subtitle = active.Count == 0
            ? "Nothing is copying."
            : eta.Length > 0 ? $"{Format.Bytes(left)} left · {eta}" : $"{Format.Bytes(left)} left";

        int doneToday = _latest.Values.Count(s => s.State == JobState.Done && s.FailedFiles == 0 && s.CreatedAtUtc.ToLocalTime().Date == _now().Date);
        DoneToday = doneToday == 0 ? "" : $"{doneToday} done today";
    }

    private void UpdateBusyDrives()
    {
        var busyRoots = _latest.Values
            .SelectMany(s => s.State switch
            {
                JobState.Running => new[] { Path.GetPathRoot(s.Source), Path.GetPathRoot(s.DestRoot) },
                JobState.Verifying => new[] { Path.GetPathRoot(s.DestRoot) }, // the source drive is free again
                _ => [],
            })
            .Where(root => !string.IsNullOrEmpty(root))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in Drives) drive.IsBusy = busyRoots.Contains(drive.Root);
    }

    private static bool IsActive(JobSnapshot s) => s.State is JobState.Queued or JobState.Running or JobState.Verifying or JobState.Paused;

    /// Unfinished jobs, jobs that finished with failures, and cancelled jobs stay visible until dismissed.
    private static bool NeedsCard(JobSnapshot s) =>
        IsActive(s) || s.State == JobState.Cancelled || (s.State == JobState.Done && s.FailedFiles > 0);
}
