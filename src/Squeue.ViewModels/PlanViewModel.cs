using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Squeue.Core.Jobs;

namespace Squeue.ViewModels;

/// The short confirmation shown before a job is queued.
public sealed partial class PlanViewModel(JobPlan plan, Action<PlanViewModel> start, Action<PlanViewModel> dismiss) : ObservableObject
{
    public JobPlan Plan { get; } = plan;
    public string Title => $"Copy {Plan.Name}";
    public string Summary => $"{Format.Count(Plan.Files.Count, "file")} · {Format.Bytes(Plan.TotalBytes)} to {Plan.DestRoot}";
    public bool HasExisting => Plan.ExistingCount > 0;

    public string? ExistingNote => Plan.ExistingCount switch
    {
        0 => null,
        1 => "1 file already exists at the destination",
        var n => $"{Format.Count(n, "file")} already exist at the destination",
    };

    public bool HasSkipped => Plan.SkippedFolders.Count > 0;

    public string? SkippedNote => Plan.SkippedFolders.Count switch
    {
        0 => null,
        1 => "1 folder couldn't be read and will be skipped",
        var n => $"{Format.Count(n, "folder")} couldn't be read and will be skipped",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Policy))]
    private bool _overwrite;

    [ObservableProperty]
    private bool _verify = true;

    public OverwritePolicy Policy => Overwrite ? OverwritePolicy.Replace : OverwritePolicy.Skip;

    [RelayCommand]
    private void Start() => start(this);

    [RelayCommand]
    private void Dismiss() => dismiss(this);
}
