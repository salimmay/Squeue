using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Squeue.Core.FileSystem;

/// Which physical disks a path lives on. Runtime topology only: never stored.
public interface IDiskTopology
{
    /// Disk keys ("disk:2", "net:NAS", "unk:\\?\Volume{…}\") for the path's volume; empty when the volume isn't there.
    IReadOnlyList<string> DisksOf(string path);
}

public readonly record struct VolumeSpace(string Root, long FreeBytes, long TotalBytes, long ClusterSize);

public interface IVolumeSpace
{
    /// Free space, size and cluster size of the path's volume; null when the volume isn't there.
    VolumeSpace? Query(string path);
}

public sealed class WindowsDiskTopology : IDiskTopology
{
    /// Server of a \\server\share or \\?\UNC\server\share path; null for local paths (including \\?\X:\ and \\.\X:\).
    private static string? NetworkServerOf(string full)
    {
        const string longUnc = @"\\?\UNC\";
        if (full.StartsWith(longUnc, StringComparison.OrdinalIgnoreCase)) return full[longUnc.Length..].Split('\\')[0];
        if (full.StartsWith(@"\\", StringComparison.Ordinal) && !full.StartsWith(@"\\?\", StringComparison.Ordinal)
            && !full.StartsWith(@"\\.\", StringComparison.Ordinal))
            return full[2..].Split('\\')[0];
        return null;
    }

    public IReadOnlyList<string> DisksOf(string path)
    {
        string full = Path.GetFullPath(path);
        if (NetworkServerOf(full) is { } server) return ["net:" + server.ToUpperInvariant()];

        if (VolumeNative.VolumePathOf(full) is not { } mount) return [];
        // Mapped network drives and subst drives have no volume GUID but are there: one unknown disk.
        if (VolumeNative.VolumeGuidOf(mount) is not { } volume)
            return Directory.Exists(mount) ? ["unk:" + mount.ToUpperInvariant()] : [];
        var disks = VolumeNative.DiskNumbersOf(volume);
        return disks is { Count: > 0 }
            ? disks.Select(n => "disk:" + n).ToList()
            : ["unk:" + volume.ToUpperInvariant()];
    }
}

public sealed class WindowsVolumeSpace : IVolumeSpace
{
    public VolumeSpace? Query(string path)
    {
        string full = Path.GetFullPath(path);
        if (VolumeNative.VolumePathOf(full) is not { } mount || !Directory.Exists(mount)) return null;
        if (!VolumeNative.GetDiskFreeSpaceEx(mount, out ulong free, out ulong total, out _)) return null;
        long cluster = VolumeNative.GetDiskFreeSpace(mount, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _)
            ? (long)sectorsPerCluster * bytesPerSector
            : 4096;
        return new VolumeSpace(mount, (long)free, (long)total, cluster);
    }
}

internal static unsafe partial class VolumeNative
{
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathName(string fileName, char* volumePathName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, char* volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);

    /// The mount point holding the path ("E:\" or a mounted folder), or null. Works for paths that don't exist yet.
    public static string? VolumePathOf(string path)
    {
        char* buffer = stackalloc char[1024];
        return GetVolumePathName(path, buffer, 1024) ? new string(buffer) : null;
    }

    /// "\\?\Volume{…}\" for a mount point, or null when no volume is mounted there (e.g. a removed drive).
    public static string? VolumeGuidOf(string mountPoint)
    {
        char* buffer = stackalloc char[64];
        return GetVolumeNameForVolumeMountPoint(mountPoint, buffer, 64) ? new string(buffer) : null;
    }

    /// Physical disk numbers under a volume, or null when Windows won't say (some virtual or USB enclosures).
    public static IReadOnlyList<int>? DiskNumbersOf(string volumeGuidPath)
    {
        // "\\?\Volume{…}\" → "\\.\Volume{…}" opens the volume itself; no access rights are needed for this query.
        string device = @"\\.\" + volumeGuidPath[4..].TrimEnd('\\');
        using var handle = Native.CreateFile(device, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, 0, Native.OPEN_EXISTING, 0, 0);
        if (handle.IsInvalid) return null;

        const int size = 4096;
        byte* output = stackalloc byte[size];
        if (!DeviceIoControl(handle, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, output, size, out _, 0)) return null;
        // VOLUME_DISK_EXTENTS: DWORD count, padding, then DISK_EXTENT { DWORD DiskNumber; pad; LARGE_INTEGER x2 } (24 bytes).
        int count = *(int*)output;
        var disks = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            int number = *(int*)(output + 8 + i * 24);
            if (!disks.Contains(number)) disks.Add(number);
        }
        return disks;
    }
}
