using CommunityToolkit.Mvvm.ComponentModel;

namespace Squeue.ViewModels;

public sealed record Choice<T>(T Value, string Label);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly bool _loading = true;

    public SettingsViewModel(ISettingsStore store)
    {
        _store = store;
        var loaded = store.Load();
        CheckAfterCopying = loaded.CheckAfterCopying;
        ExistingFiles = loaded.ExistingFiles;
        WhenDone = loaded.WhenDone;
        HistoryDays = loaded.HistoryDays;
        _loading = false;
    }

    [ObservableProperty] private bool _checkAfterCopying;
    [ObservableProperty] private ExistingFiles _existingFiles;
    [ObservableProperty] private WhenDone _whenDone;
    [ObservableProperty] private int _historyDays;

    public AppSettings Current => new()
    {
        CheckAfterCopying = CheckAfterCopying,
        ExistingFiles = ExistingFiles,
        WhenDone = WhenDone,
        HistoryDays = HistoryDays,
    };

    public IReadOnlyList<Choice<ExistingFiles>> ExistingFilesChoices { get; } =
        [new(ViewModels.ExistingFiles.Skip, "Skip it"), new(ViewModels.ExistingFiles.Replace, "Replace it")];

    public IReadOnlyList<Choice<WhenDone>> WhenDoneChoices { get; } =
        [new(ViewModels.WhenDone.Sound, "Play a sound"), new(ViewModels.WhenDone.Flash, "Flash the taskbar button"), new(ViewModels.WhenDone.Nothing, "Do nothing")];

    public IReadOnlyList<Choice<int>> HistoryDaysChoices { get; } =
        [new(30, "30 days"), new(90, "90 days"), new(0, "Forever")];

    partial void OnCheckAfterCopyingChanged(bool value) => Save();
    partial void OnExistingFilesChanged(ExistingFiles value) => Save();
    partial void OnWhenDoneChanged(WhenDone value) => Save();
    partial void OnHistoryDaysChanged(int value) => Save();

    private void Save()
    {
        if (!_loading) _store.Save(Current);
    }
}
