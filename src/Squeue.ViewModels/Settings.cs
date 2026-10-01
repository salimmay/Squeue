using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Squeue.ViewModels;

public enum ExistingFiles { Skip, Replace }

public enum WhenDone { Sound, Flash, Nothing }

public sealed record AppSettings
{
    public bool CheckAfterCopying { get; init; } = true;
    public ExistingFiles ExistingFiles { get; init; } = ExistingFiles.Skip;
    public WhenDone WhenDone { get; init; } = WhenDone.Sound;
    public int HistoryDays { get; init; } = 90;
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            return Clamp(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    /// Values outside what the settings page offers go back to their defaults.
    private static AppSettings Clamp(AppSettings loaded)
    {
        var defaults = new AppSettings();
        return loaded with
        {
            ExistingFiles = Enum.IsDefined(loaded.ExistingFiles) ? loaded.ExistingFiles : defaults.ExistingFiles,
            WhenDone = Enum.IsDefined(loaded.WhenDone) ? loaded.WhenDone : defaults.WhenDone,
            HistoryDays = loaded.HistoryDays is 0 or 30 or 90 ? loaded.HistoryDays : defaults.HistoryDays,
        };
    }

    public void Save(AppSettings settings)
    {
        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Squeue: could not save settings to {path}: {ex.Message}");
        }
    }
}
