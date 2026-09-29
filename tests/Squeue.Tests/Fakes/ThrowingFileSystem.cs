using Squeue.Core.FileSystem;

namespace Squeue.Tests.Fakes;

/// Wraps a real file system and fails the Nth OpenSource once with a Windows error, like a drive that is full or gone.
public sealed class ThrowingFileSystem(IFileSystem inner, int win32Error, int failOnOpenSourceNumber) : IFileSystem
{
    private int _opens;

    public ISourceFile OpenSource(string path)
    {
        if (Interlocked.Increment(ref _opens) == failOnOpenSourceNumber) throw new FsException("open", path, win32Error);
        return inner.OpenSource(path);
    }

    public ITempFile CreateTemp(string path) => inner.CreateTemp(path);
    public IVerifyFile OpenForVerify(string path) => inner.OpenForVerify(path);
    public FileIdentity? TryGetIdentity(string path) => inner.TryGetIdentity(path);
    public bool DeleteIfSameObject(string path, UInt128 fileId) => inner.DeleteIfSameObject(path, fileId);
    public bool FlushIfSameObject(string path, UInt128 fileId) => inner.FlushIfSameObject(path, fileId);
    public void CreateDirectory(string path) => inner.CreateDirectory(path);
}
