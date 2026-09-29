using CommunityToolkit.Mvvm.ComponentModel;

namespace Squeue.ViewModels;

public sealed record DriveInfoLite(string Root, string Label);

public interface IDriveSource
{
    IReadOnlyList<DriveInfoLite> ReadyDrives();
}

public sealed partial class DriveViewModel(DriveInfoLite drive) : ObservableObject
{
    public string Root { get; } = drive.Root;
    public string Name { get; } = drive.Label;

    [ObservableProperty]
    private bool _isBusy;
}
