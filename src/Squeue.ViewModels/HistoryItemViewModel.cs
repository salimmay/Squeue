using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Squeue.Core.Jobs;
using Squeue.Core.State;

namespace Squeue.ViewModels;

/// One finished or cancelled job in the History tab.
public sealed partial class HistoryItemViewModel(long id, IJobQueue queue, Action<string>? openFolder, Func<DateTime> now) : ObservableObject
{
    private string _destRoot = "";
    private bool _failedFilesRequested;

    public long Id { get; } = id;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _route = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private bool _needsAttention;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private string _dayLabel = "";
    [ObservableProperty] private DateTime _finishedLocal;
    [ObservableProperty] private bool _showsFailedFiles;

    public ObservableCollection<FailedFile> FailedFiles { get; } = [];

    public void Update(JobSnapshot s)
    {
        _destRoot = s.DestRoot;
        Title = s.Name;
        Route = $"{s.Source} → {s.DestRoot}";
        var finished = (s.FinishedAtUtc ?? s.CreatedAtUtc).ToLocalTime();
        FinishedLocal = finished;
        DayLabel = DayLabelFor(finished.Date, now().Date);
        Details = string.Create(CultureInfo.InvariantCulture, $"{finished:HH:mm}") + $" · {Format.Count(s.TotalFiles, "file")} · {Format.Bytes(s.TotalBytes)}";
        bool failed = s.State == JobState.Done && s.FailedFiles > 0;
        Result = s.State switch
        {
            JobState.Cancelled => "Cancelled",
            _ when failed => $"{s.FailedFiles} failed",
            _ when s.Checked => "All checked",
            _ => "Copied, not checked",
        };
        NeedsAttention = failed;
        Reason = failed ? s.LastError ?? "" : "";
        CanRetry = failed; // the engine ignores RetryFailed on cancelled jobs
    }

    public void SetFailedFiles(IReadOnlyList<FailedFile> files)
    {
        FailedFiles.Clear();
        foreach (var file in files) FailedFiles.Add(file);
    }

    private static string DayLabelFor(DateTime day, DateTime today) =>
        day == today ? "Today"
        : day == today.AddDays(-1) ? "Yesterday"
        : day.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    [RelayCommand]
    private void RetryFailed() => queue.RetryFailed(Id);

    [RelayCommand]
    private void ToggleFailedFiles()
    {
        ShowsFailedFiles = !ShowsFailedFiles;
        if (ShowsFailedFiles && !_failedFilesRequested)
        {
            _failedFilesRequested = true;
            queue.RequestFailedFiles(Id);
        }
    }

    [RelayCommand]
    private void OpenFolder() => openFolder?.Invoke(_destRoot);
}
