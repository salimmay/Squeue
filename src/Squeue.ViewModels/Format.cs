using System.Globalization;

namespace Squeue.ViewModels;

/// Short, human text for sizes, speeds and times.
public static class Format
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => string.Create(Culture, $"{bytes} B"),
        < 1024L * 1024 => string.Create(Culture, $"{bytes / 1024.0:0} KB"),
        < 1024L * 1024 * 1024 => string.Create(Culture, $"{bytes / 1048576.0:0.#} MB"),
        _ => string.Create(Culture, $"{bytes / 1073741824.0:0.#} GB"),
    };

    public static string Speed(double bytesPerSecond) => $"{Bytes((long)bytesPerSecond)}/s";

    public static string TimeLeft(TimeSpan left)
    {
        if (left.TotalSeconds < 60) return "under a minute left";
        if (left.TotalMinutes < 60) return string.Create(Culture, $"{Math.Ceiling(left.TotalMinutes):0} min left");
        return string.Create(Culture, $"{(int)left.TotalHours} h {left.Minutes} min left");
    }

    public static string Count(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString("N0", Culture)} {noun}s";
}
