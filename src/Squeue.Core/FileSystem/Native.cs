using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Squeue.Core.FileSystem;

internal static unsafe partial class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint DELETE = 0x00010000;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;

    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint FILE_SHARE_DELETE = 0x4;
    public const uint FILE_SHARE_ALL = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

    public const uint CREATE_NEW = 1;
    public const uint OPEN_EXISTING = 3;

    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    // FILE_INFO_BY_HANDLE_CLASS values
    public const int FileBasicInfo = 0;
    public const int FileStandardInfo = 1;
    public const int FileRenameInfo = 3;
    public const int FileDispositionInfo = 4;
    public const int FileAllocationInfo = 5;
    public const int FileEndOfFileInfo = 6;
    public const int FileIdInfo = 18;
    public const int FileDispositionInfoEx = 21;

    public const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;
    public const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2;
    public const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_FILE_EXISTS = 80;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_ALREADY_EXISTS = 183;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(SafeFileHandle file, BY_HANDLE_FILE_INFORMATION* info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushFileBuffers(SafeFileHandle file);

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_STANDARD_INFO
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public fixed byte FileId[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    /// Full path with the \\?\ prefix so paths over 260 characters work.
    public static string LongPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full[2..];
        return @"\\?\" + full;
    }

    public static FsException LastError(string operation, string path) =>
        new(operation, path, Marshal.GetLastPInvokeError());

    public static FileIdentity ReadIdentity(SafeFileHandle handle, string path)
    {
        FILE_BASIC_INFO basic;
        FILE_STANDARD_INFO standard;
        if (!GetFileInformationByHandleEx(handle, FileBasicInfo, &basic, (uint)sizeof(FILE_BASIC_INFO)))
            throw LastError("read the attributes of", path);
        if (!GetFileInformationByHandleEx(handle, FileStandardInfo, &standard, (uint)sizeof(FILE_STANDARD_INFO)))
            throw LastError("read the size of", path);

        ulong volume;
        UInt128 fileId;
        FILE_ID_INFO id;
        if (GetFileInformationByHandleEx(handle, FileIdInfo, &id, (uint)sizeof(FILE_ID_INFO)))
        {
            volume = id.VolumeSerialNumber;
            fileId = FileIdentity.FileIdFromBytes(new ReadOnlySpan<byte>(id.FileId, 16));
        }
        else
        {
            // FAT/exFAT and some network filesystems don't support FileIdInfo; fall back to the 64-bit index.
            BY_HANDLE_FILE_INFORMATION info;
            if (!GetFileInformationByHandle(handle, &info)) throw LastError("read the file id of", path);
            volume = info.VolumeSerialNumber;
            fileId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        }

        return new FileIdentity(volume, fileId, standard.EndOfFile,
            basic.CreationTime, basic.LastWriteTime, basic.ChangeTime, basic.FileAttributes);
    }
}
