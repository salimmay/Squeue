using System.Globalization;
using Squeue.Core.FileSystem;

namespace Squeue.Core.Jobs;

/// Admission check for a job's destination: will everything it still has to write fit, with room to spare?
/// This is admission control, not a guarantee — other programs can still fill the drive.
public static class Capacity
{
    public static long RoundUp(long size, long cluster) =>
        cluster <= 0 ? size : (size + cluster - 1) / cluster * cluster;

    /// Space the files will take: each rounded up to whole clusters. Overwrites get no credit, because the old
    /// file stays until the verified new one replaces it.
    public static long Need(IEnumerable<long> sizes, long cluster) => sizes.Sum(s => RoundUp(s, cluster));

    /// Always leave max(1 GB, 1 % of the volume) free, but never more than a tenth of it, so small destinations stay usable.
    public static long Headroom(long totalBytes) => Math.Min(Math.Max(1L << 30, totalBytes / 100), totalBytes / 10);

    /// Null when the job fits; otherwise a sentence saying what's missing.
    public static string? Problem(VolumeSpace space, long need, long reservedByRunningJobs)
    {
        if (need <= 0) return null;
        long headroom = Headroom(space.TotalBytes);
        long available = space.FreeBytes - reservedByRunningJobs - headroom;
        if (available >= need) return null;
        return $"Not enough space on {space.Root}: this needs {Text(need)}, and {Text(Math.Max(0, available))} is free " +
               $"after keeping {Text(headroom)} spare. Free some space and resume.";
    }

    public static string Text(long bytes) => bytes >= 1L << 30
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1073741824.0:0.#} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.#} MB");
}
