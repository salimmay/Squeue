using Squeue.Core.FileSystem;

namespace Squeue.Tests.Fakes;

public enum FsOp { OpenSource, CreateTemp, Preallocate, Write, SetLength, SetTimes, Flush, Rename, OpenForVerify, VerifyRead, TryGetIdentity, Delete }

/// Stands in for the process dying. Not an IOException, so no error handler in the copier catches it.
public sealed class SimulatedCrashException(string point) : Exception($"Simulated crash {point}");

/// Wraps a real file system and throws SimulatedCrashException just before or just after a chosen operation.
public sealed class FaultyFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly Dictionary<FsOp, int> _counts = [];
    private readonly object _lock = new();

    public (FsOp Op, bool After, int Occurrence)? CrashAt { get; set; }
    public bool CorruptVerifyReads { get; set; }
    public bool Crashed { get; private set; }

    public ISourceFile OpenSource(string path) => Open(FsOp.OpenSource, () => new Source(inner.OpenSource(path)));
    public ITempFile CreateTemp(string path) => Open(FsOp.CreateTemp, () => new Temp(inner.CreateTemp(path), this));
    public IVerifyFile OpenForVerify(string path) => Open(FsOp.OpenForVerify, () => new Verify(inner.OpenForVerify(path), this));

    public FileIdentity? TryGetIdentity(string path)
    {
        Hit(FsOp.TryGetIdentity, after: false);
        var result = inner.TryGetIdentity(path);
        Hit(FsOp.TryGetIdentity, after: true);
        return result;
    }

    public bool FlushIfSameObject(string path, UInt128 fileId) => inner.FlushIfSameObject(path, fileId);

    public bool DeleteIfSameObject(string path, UInt128 fileId)
    {
        Hit(FsOp.Delete, after: false);
        bool result = inner.DeleteIfSameObject(path, fileId);
        Hit(FsOp.Delete, after: true);
        return result;
    }

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    internal void Hit(FsOp op, bool after)
    {
        lock (_lock)
        {
            if (!after) _counts[op] = _counts.GetValueOrDefault(op) + 1;
            if (CrashAt is { } crash && crash.Op == op && crash.After == after && _counts.GetValueOrDefault(op) == crash.Occurrence)
            {
                CrashAt = null;
                Crashed = true;
                throw new SimulatedCrashException($"{(after ? "after" : "before")} {op} #{crash.Occurrence}");
            }
        }
    }

    private T Open<T>(FsOp op, Func<T> open) where T : IDisposable
    {
        Hit(op, after: false);
        T handle = open();
        try { Hit(op, after: true); }
        catch { handle.Dispose(); throw; } // a dying process closes its handles
        return handle;
    }

    private sealed class Source(ISourceFile inner) : ISourceFile
    {
        public FileIdentity Identity => inner.Identity;
        public int Read(Span<byte> buffer, long offset) => inner.Read(buffer, offset);
        public void Dispose() => inner.Dispose();
    }

    private sealed class Temp(ITempFile inner, FaultyFileSystem fs) : ITempFile
    {
        public FileIdentity GetIdentity() => inner.GetIdentity();
        public void Preallocate(long size) { fs.Hit(FsOp.Preallocate, false); inner.Preallocate(size); fs.Hit(FsOp.Preallocate, true); }
        public void Write(ReadOnlySpan<byte> data, long offset) { fs.Hit(FsOp.Write, false); inner.Write(data, offset); fs.Hit(FsOp.Write, true); }
        public void SetLength(long length) { fs.Hit(FsOp.SetLength, false); inner.SetLength(length); fs.Hit(FsOp.SetLength, true); }

        public void SetTimesAndAttributes(long creationTime, long lastWriteTime, uint attributes)
        {
            fs.Hit(FsOp.SetTimes, false);
            inner.SetTimesAndAttributes(creationTime, lastWriteTime, attributes);
            fs.Hit(FsOp.SetTimes, true);
        }

        public void Flush() { fs.Hit(FsOp.Flush, false); inner.Flush(); fs.Hit(FsOp.Flush, true); }

        public void RenameTo(string finalPath, bool replaceExisting)
        {
            fs.Hit(FsOp.Rename, false);
            inner.RenameTo(finalPath, replaceExisting);
            fs.Hit(FsOp.Rename, true);
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class Verify(IVerifyFile inner, FaultyFileSystem fs) : IVerifyFile
    {
        public int Read(Span<byte> buffer, long offset)
        {
            fs.Hit(FsOp.VerifyRead, false);
            int read = inner.Read(buffer, offset);
            if (fs.CorruptVerifyReads && read > 0) buffer[0] ^= 0xFF;
            fs.Hit(FsOp.VerifyRead, true);
            return read;
        }

        public void Dispose() => inner.Dispose();
    }
}
