using Microsoft.Win32.SafeHandles;

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

    public ITempFile CreateTemp(string path) => throw new NotImplementedException("Task 2");

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

    public bool DeleteIfSameObject(string path, UInt128 fileId) => throw new NotImplementedException("Task 2");

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
