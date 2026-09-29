namespace Squeue.Core.FileSystem;

/// The file operations the copy protocol needs. Tests wrap it to inject crashes.
public interface IFileSystem
{
    /// Opens a source for reading. Others can still read it, but can't write or delete it while this is open.
    ISourceFile OpenSource(string path);

    /// Creates a new temp file (fails if the name exists), open for read, write and rename.
    ITempFile CreateTemp(string path);

    /// Opens a file for unbuffered reading, bypassing the Windows file cache.
    IVerifyFile OpenForVerify(string path);

    /// Identity of whatever is at the path now, without following reparse points. Null if nothing is there.
    FileIdentity? TryGetIdentity(string path);

    /// Deletes the path only if it is still the object with this file id. False if it's missing or a different object.
    bool DeleteIfSameObject(string path, UInt128 fileId);

    /// Flushes the file at the path to disk if it is still the object with this file id. False if it's missing or a different object.
    bool FlushIfSameObject(string path, UInt128 fileId);

    void CreateDirectory(string path);
}

public interface ISourceFile : IDisposable
{
    FileIdentity Identity { get; }
    int Read(Span<byte> buffer, long offset);
}

public interface ITempFile : IDisposable
{
    FileIdentity GetIdentity();
    void Preallocate(long size);
    void Write(ReadOnlySpan<byte> data, long offset);
    void SetLength(long length);
    void SetTimesAndAttributes(long creationTime, long lastWriteTime, uint attributes);
    void Flush();

    /// Renames through this handle. Throws FsException with IsAlreadyExists when the target exists and replaceExisting is false.
    void RenameTo(string finalPath, bool replaceExisting);
}

public interface IVerifyFile : IDisposable
{
    /// The buffer must come from AlignedBuffer and the offset must be a multiple of 4096.
    int Read(Span<byte> buffer, long offset);
}
