using Squeue.Core.FileSystem;

namespace Squeue.Core.Jobs;

public sealed record PlannedFile(string SourcePath, string DestPath, long Size, FileIdentity? ExistingDest);

public sealed record JobPlan(string Name, string Source, string DestRoot, IReadOnlyList<PlannedFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.Size);
    public int ExistingCount => Files.Count(f => f.ExistingDest is not null);
}

/// Turns what the user picked into the list of files to copy.
public static class JobPlanner
{
    // Links and junctions are not followed: they could loop or reach outside what the user picked.
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    /// A folder keeps its own name under the destination: F:\DCIM → E:\Backup\DCIM\...
    public static JobPlan Plan(IFileSystem fs, IReadOnlyList<string> sources, string destRoot)
    {
        if (sources.Count == 0) throw new ArgumentException("Choose at least one file or folder to copy.", nameof(sources));
        string dest = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destRoot));
        var files = new List<PlannedFile>();

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
            foreach (string file in Directory.EnumerateFiles(source, "*", Walk).Order(StringComparer.OrdinalIgnoreCase))
                Add(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }

        string first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sources[0]));
        string name = sources.Count == 1 ? FolderName(first) : $"{sources.Count} items";
        string sourceLabel = sources.Count == 1 ? first : Path.GetDirectoryName(first) ?? first;
        return new JobPlan(name, sourceLabel, dest, files);

        void Add(string src, string dst) => files.Add(new PlannedFile(src, dst, new FileInfo(src).Length, fs.TryGetIdentity(dst)));
    }

    // A drive root such as "F:\" has no folder name; use "Drive F" instead.
    private static string FolderName(string path) =>
        Path.GetFileName(path) is { Length: > 0 } name ? name : $"Drive {path[0]}";

    private static bool IsSameOrInside(string path, string folder)
    {
        string f = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        string p = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }
}
