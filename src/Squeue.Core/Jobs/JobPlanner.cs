using Squeue.Core.FileSystem;

namespace Squeue.Core.Jobs;

public sealed record PlannedFile(string SourcePath, string DestPath, long Size, FileIdentity? ExistingDest)
{
    /// The existing destination has the source's size and last-write time, so it is most likely the same file.
    public bool ExistingLooksSame { get; init; }
}

public sealed record JobPlan(string Name, string Source, string DestRoot, IReadOnlyList<PlannedFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.Size);
    public int ExistingCount => Files.Count(f => f.ExistingDest is not null);

    /// Existing destination files that don't look like the source (different size or time).
    public int DifferentCount => Files.Count(f => f.ExistingDest is not null && !f.ExistingLooksSame);

    /// Folders that couldn't be read and are not included.
    public IReadOnlyList<string> SkippedFolders { get; init; } = [];
}

/// Turns what the user picked into the list of files to copy.
public static class JobPlanner
{
    // Links and junctions are not followed: they could loop or reach outside what the user picked.
    private static readonly EnumerationOptions NoRecurse = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    };

    /// A folder keeps its own name under the destination: F:\DCIM → E:\Backup\DCIM\...
    public static JobPlan Plan(IFileSystem fs, IReadOnlyList<string> sources, string destRoot)
    {
        if (sources.Count == 0) throw new ArgumentException("Choose at least one file or folder to copy.", nameof(sources));
        string dest = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destRoot));
        var files = new List<PlannedFile>();
        var skipped = new List<string>();

        foreach (string raw in sources)
        {
            string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            if (File.Exists(source))
            {
                Add(source, Path.Combine(dest, Path.GetFileName(source)));
                continue;
            }
            if (!Directory.Exists(source)) throw new FileNotFoundException($"'{source}' doesn't exist.", source);
            if (IsSameOrInside(dest, source)) throw new ArgumentException($"Can't copy '{source}' into itself.", nameof(destRoot));

            string target = Path.Combine(dest, FolderName(source));
            WalkFolder(source, target, fs, files, skipped);
        }

        string first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sources[0]));
        string name = sources.Count == 1 ? FolderName(first) : $"{sources.Count} items";
        string sourceLabel = sources.Count == 1 ? first : Path.GetDirectoryName(first) ?? first;
        return new JobPlan(name, sourceLabel, dest, files) { SkippedFolders = skipped };

        void Add(string src, string dst) => files.Add(Planned(fs, new FileInfo(src), dst));
    }

    private static PlannedFile Planned(IFileSystem fs, FileInfo source, string destPath)
    {
        var existing = fs.TryGetIdentity(destPath);
        bool looksSame = existing is { } e
            && e.Size == source.Length
            && e.LastWriteTime == source.LastWriteTimeUtc.ToFileTimeUtc();
        return new PlannedFile(source.FullName, destPath, source.Length, existing) { ExistingLooksSame = looksSame };
    }

    private static void WalkFolder(string folder, string targetPrefix, IFileSystem fs, List<PlannedFile> files, List<string> skipped)
    {
        // List and add files in this folder
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", NoRecurse).Order(StringComparer.OrdinalIgnoreCase))
            {
                string destPath = Path.Combine(targetPrefix, Path.GetFileName(file));
                files.Add(Planned(fs, new FileInfo(file), destPath));
            }
        }
        catch (UnauthorizedAccessException) { skipped.Add(folder); return; }
        catch (IOException) { skipped.Add(folder); return; }

        // Recursively walk subfolders
        try
        {
            foreach (string subfolder in Directory.EnumerateDirectories(folder, "*", NoRecurse).Order(StringComparer.OrdinalIgnoreCase))
            {
                string folderName = Path.GetFileName(subfolder);
                // Skip Windows system folders silently
                if (folderName.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                    folderName.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase))
                    continue;

                string newTarget = Path.Combine(targetPrefix, folderName);
                WalkFolder(subfolder, newTarget, fs, files, skipped);
            }
        }
        catch (UnauthorizedAccessException) { skipped.Add(folder); }
        catch (IOException) { skipped.Add(folder); }
    }

    // A drive root such as "F:\" has no folder name; use "Drive F" instead.
    private static string FolderName(string path) =>
        Path.GetFileName(path) is { Length: > 0 } name ? name : $"Drive {path[0]}";

    private static string WithSeparator(string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        return trimmed.EndsWith(Path.DirectorySeparatorChar) ? trimmed : trimmed + Path.DirectorySeparatorChar;
    }

    internal static bool IsSameOrInside(string path, string folder) =>
        WithSeparator(path).StartsWith(WithSeparator(folder), StringComparison.OrdinalIgnoreCase);
}
