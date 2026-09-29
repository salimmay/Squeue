using Squeue.Core.FileSystem;

namespace Squeue.Tests.Fakes;

/// Pretends folders are on different disks: the last path segment named in the map decides the disk.
public sealed class FolderTopology(params (string Folder, string Disk)[] map) : IDiskTopology
{
    public IReadOnlyList<string> DisksOf(string path)
    {
        var segments = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            foreach (var (folder, disk) in map)
            {
                if (string.Equals(segments[i], folder, StringComparison.OrdinalIgnoreCase)) return [disk];
            }
        }
        return ["other"];
    }
}
