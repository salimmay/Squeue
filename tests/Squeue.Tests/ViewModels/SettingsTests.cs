using Squeue.ViewModels;

namespace Squeue.Tests.ViewModels;

public sealed class SettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "squeue-settings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Defaults_when_no_file_exists()
    {
        var settings = new JsonSettingsStore(FilePath).Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.True(settings.CheckAfterCopying);
        Assert.Equal(ExistingFiles.Skip, settings.ExistingFiles);
        Assert.Equal(WhenDone.Sound, settings.WhenDone);
        Assert.Equal(90, settings.HistoryDays);
    }

    [Fact]
    public void Saved_settings_are_loaded_back()
    {
        var saved = new AppSettings { CheckAfterCopying = false, ExistingFiles = ExistingFiles.Replace, WhenDone = WhenDone.Flash, HistoryDays = 30 };
        new JsonSettingsStore(FilePath).Save(saved);

        Assert.Equal(saved, new JsonSettingsStore(FilePath).Load());
    }

    [Fact]
    public void A_broken_settings_file_falls_back_to_defaults()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, "{ not json");

        Assert.Equal(new AppSettings(), new JsonSettingsStore(FilePath).Load());
    }

    [Fact]
    public void Changing_a_setting_saves_it()
    {
        var viewModel = new SettingsViewModel(new JsonSettingsStore(FilePath));

        viewModel.WhenDone = WhenDone.Nothing;
        viewModel.HistoryDays = 0;

        var loaded = new JsonSettingsStore(FilePath).Load();
        Assert.Equal(WhenDone.Nothing, loaded.WhenDone);
        Assert.Equal(0, loaded.HistoryDays);
        Assert.Equal(loaded, viewModel.Current);
    }

    [Fact]
    public void Unknown_values_fall_back_to_defaults()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, "{ \"WhenDone\": \"Dance\" }");

        Assert.Equal(new AppSettings(), new JsonSettingsStore(FilePath).Load());
    }
}
