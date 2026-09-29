using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Squeue.Core.Jobs;
using Squeue.Core.State;

namespace Squeue.ViewModels;

/// <param name="dismiss">Removes this card from the queue window; called by DismissCommand.</param>
public sealed partial class JobCardViewModel(long id, IJobQueue queue, Action<JobCardViewModel> dismiss) : ObservableObject
{
    public long Id { get; } = id;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _route = "";
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private string _percent = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _timeLeft = "";
    [ObservableProperty] private JobState _state;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canResume;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _canDismiss;

    /// Why the job is paused, or the last failure of a job that finished with failures.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string _detail = "";

    public bool HasDetail => Detail.Length > 0;

    public void Update(JobSnapshot s)
    {
        Title = s.Name;
        Route = $"{s.Source} → {s.DestRoot}";
        Fraction = s.Fraction;
        Percent = $"{(s.State == JobState.Done ? Math.Round(s.Fraction * 100) : Math.Floor(s.Fraction * 100)):0}%";
        State = s.State;
        IsRunning = s.State == JobState.Running;
        CanPause = s.State is JobState.Running or JobState.Queued;
        CanResume = s.State == JobState.Paused;
        CanCancel = s.State is JobState.Queued or JobState.Running or JobState.Paused;
        CanDismiss = s.State is JobState.Done or JobState.Cancelled;
        Status = StatusText(s);
        Detail = (s.State == JobState.Done && s.FailedFiles > 0) || s.State == JobState.Paused ? s.LastError ?? "" : "";
        TimeLeft = s.State == JobState.Running
            ? Format.TimeLeftFor(s.TotalBytes - s.DoneBytes, s.BytesPerSecond)
            : "";
    }

    public static string StatusText(JobSnapshot s) => s.State switch
    {
        JobState.Running => s.BytesPerSecond > 0 ? $"Copying · {Format.Speed(s.BytesPerSecond)}" : "Copying",
        JobState.Queued => $"Waiting · {Format.Count(s.TotalFiles, "file")}, {Format.Bytes(s.TotalBytes)}",
        JobState.Paused => s.LastError is { Length: > 0 } reason ? $"Paused · {reason}" : "Paused",
        JobState.Cancelled => "Cancelled",
        _ => s.FailedFiles > 0 ? $"Done · {s.FailedFiles} failed" : "Done",
    };

    [RelayCommand]
    private void Pause() => queue.Pause(Id);

    [RelayCommand]
    private void Resume() => queue.Resume(Id);

    [RelayCommand]
    private void Cancel() => queue.Cancel(Id);

    [RelayCommand]
    private void Dismiss() => dismiss(this);
}
