using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Squeue.Core.Jobs;
using Squeue.Core.State;

namespace Squeue.ViewModels;

public sealed partial class JobCardViewModel(long id, IJobQueue queue) : ObservableObject
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

    public void Update(JobSnapshot s)
    {
        Title = s.Name;
        Route = $"{s.Source} → {s.DestRoot}";
        Fraction = s.Fraction;
        Percent = $"{Math.Round(s.Fraction * 100):0}%";
        State = s.State;
        IsRunning = s.State == JobState.Running;
        CanPause = s.State is JobState.Running or JobState.Queued;
        CanResume = s.State == JobState.Paused;
        Status = StatusText(s);
        TimeLeft = s.State == JobState.Running && s.BytesPerSecond > 0
            ? Format.TimeLeft(TimeSpan.FromSeconds((s.TotalBytes - s.DoneBytes) / s.BytesPerSecond))
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
}
