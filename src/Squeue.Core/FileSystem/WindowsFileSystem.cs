using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Squeue.Core.FileSystem;

public sealed class WindowsFileSystem : IFileSystem
{
    public ISourceFile OpenSource(string path)
    {
        var handle = Open(path, Native.GENERIC_READ, Native.FILE_SHARE_READ, Native.OPEN_EXISTING,
            Native.FILE_FLAG_SEQUENTIAL_SCAN, "open");
        try { return new WindowsSourceFile(handle, Native.ReadIdentity(handle, path)); }
        catch { handle.Dispose(); throw; }
    }

    public ITempFile CreateTemp(string path)
    {
        // DELETE access is needed to rename through the handle. Share read only: verification may read it,
        // nobody else may write, rename or delete it while it's open.
        var handle = Open(path, Native.GENERIC_READ | Native.GENERIC_WRITE | Native.DELETE, Native.FILE_SHARE_READ,
            Native.CREATE_NEW, 0, "create temp file");
        return new WindowsTempFile(handle, path);
    }

    public IVerifyFile OpenForVerify(string path) => throw new NotImplementedException("Task 3");

    public FileIdentity? TryGetIdentity(string path)
    {
        SafeFileHandle handle;
        try
        {
            handle = Open(path, Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL, Native.OPEN_EXISTING,
                Native.FILE_FLAG_BACKUP_SEMANTICS | Native.FILE_FLAG_OPEN_REPARSE_POINT, "inspect");
        }
        catch (FsException ex) when (ex.IsNotFound)
        {
            return null;
        }
        using (handle) return Native.ReadIdentity(handle, path);
    }

    public unsafe bool DeleteIfSameObject(string path, UInt128 fileId)
    {
        SafeFileHandle handle;
        try
        {
            handle = Open(path, Native.DELETE | Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL, Native.OPEN_EXISTING,
                Native.FILE_FLAG_OPEN_REPARSE_POINT, "open for cleanup");
        }
        catch (FsException ex) when (ex.IsNotFound)
        {
            return false;
        }

        using (handle)
        {
            if (Native.ReadIdentity(handle, path).FileId != fileId) return false;

            // Delete through the handle we just checked, so the delete applies to this object, not whatever is at the path.
            uint flags = Native.FILE_DISPOSITION_FLAG_DELETE | Native.FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
                       | Native.FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;
            if (Native.SetFileInformationByHandle(handle, Native.FileDispositionInfoEx, &flags, sizeof(uint))) return true;

            int error = Marshal.GetLastPInvokeError();
            if (error is not (Native.ERROR_INVALID_PARAMETER or Native.ERROR_NOT_SUPPORTED))
                throw new FsException("delete", path, error);

            byte delete = 1; // filesystems without FileDispositionInfoEx (FAT, exFAT)
            if (!Native.SetFileInformationByHandle(handle, Native.FileDispositionInfo, &delete, 1))
                throw Native.LastError("delete", path);
            return true;
        }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    internal static SafeFileHandle Open(string path, uint access, uint share, uint disposition, uint flags, string operation)
    {
        var handle = Native.CreateFile(Native.LongPath(path), access, share, 0, disposition,
            flags | Native.FILE_ATTRIBUTE_NORMAL, 0);
        if (handle.IsInvalid)
        {
            var error = Native.LastError(operation, path);
            handle.Dispose();
            throw error;
        }
        return handle;
    }
}

internal sealed class WindowsSourceFile(SafeFileHandle handle, FileIdentity identity) : ISourceFile
{
    public FileIdentity Identity { get; } = identity;
    public int Read(Span<byte> buffer, long offset) => RandomAccess.Read(handle, buffer, offset);
    public void Dispose() => handle.Dispose();
}

internal sealed unsafe class WindowsTempFile(SafeFileHandle handle, string path) : ITempFile
{
    public FileIdentity GetIdentity() => Native.ReadIdentity(handle, path);

    public void Preallocate(long size)
    {
        long value = size;
        Set(Native.FileAllocationInfo, &value, sizeof(long), "preallocate");
    }

    public void Write(ReadOnlySpan<byte> data, long offset) => RandomAccess.Write(handle, data, offset);

    public void SetLength(long length)
    {
        long value = length;
        Set(Native.FileEndOfFileInfo, &value, sizeof(long), "set the length of");
    }

    public void SetTimesAndAttributes(long creationTime, long lastWriteTime, uint attributes)
    {
        // Zero means "leave unchanged" for every field.
        var info = new Native.FILE_BASIC_INFO
        {
            CreationTime = creationTime,
            LastWriteTime = lastWriteTime,
            FileAttributes = attributes,
        };
        Set(Native.FileBasicInfo, &info, sizeof(Native.FILE_BASIC_INFO), "set the times of");
    }

    public void Flush()
    {
        if (!Native.FlushFileBuffers(handle)) throw Native.LastError("flush", path);
    }

    public void RenameTo(string finalPath, bool replaceExisting)
    {
        // FILE_RENAME_INFO on x64: ReplaceIfExists at 0, RootDirectory at 8, FileNameLength at 16, FileName at 20.
        string target = Native.LongPath(finalPath);
        int nameBytes = target.Length * sizeof(char);
        int size = 24 + nameBytes;
        byte* buffer = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            buffer[0] = replaceExisting ? (byte)1 : (byte)0;
            *(nint*)(buffer + 8) = 0;
            *(uint*)(buffer + 16) = (uint)nameBytes;
            fixed (char* name = target) Buffer.MemoryCopy(name, buffer + 20, nameBytes, nameBytes);
            if (!Native.SetFileInformationByHandle(handle, Native.FileRenameInfo, buffer, (uint)size))
                throw Native.LastError("publish", finalPath);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
        path = finalPath;
    }

    public void Dispose() => handle.Dispose();

    private void Set(int infoClass, void* info, int size, string operation)
    {
        if (!Native.SetFileInformationByHandle(handle, infoClass, info, (uint)size)) throw Native.LastError(operation, path);
    }
}
