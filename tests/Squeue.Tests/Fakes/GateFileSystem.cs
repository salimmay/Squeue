using System.Collections.Concurrent;
using Squeue.Core.FileSystem;

namespace Squeue.Tests.Fakes;

/// Passes everything through, but holds reads under chosen folders until the test opens the gate
/// (at most 30 s, so a failing test can't hang the run). It can also damage one verification read of a chosen file.
public sealed class GateFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly ConcurrentDictionary<string, ManualResetEventSlim> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ManualResetEventSlim> _verifies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _corruptOnce = new(StringComparer.OrdinalIgnoreCase);

    public ManualResetEventSlim HoldSourcesUnder(string folder) => _sources.GetOrAdd(Path.GetFullPath(folder), _ => new(false));
    public ManualResetEventSlim HoldVerificationUnder(string folder) => _verifies.GetOrAdd(Path.GetFullPath(folder), _ => new(false));

    public ISourceFile OpenSource(string path) { Wait(_sources, path); return inner.OpenSource(path); }
    /// The next verification of this file reads a flipped first byte, so it fails once.
    public void CorruptNextVerificationOf(string path) => _corruptOnce[Path.GetFullPath(path)] = 0;

    public IVerifyFile OpenForVerify(string path)
    {
        Wait(_verifies, path);
        var file = inner.OpenForVerify(path);
        return _corruptOnce.TryRemove(Path.GetFullPath(path), out _) ? new Corrupting(file) : file;
    }
    public ITempFile CreateTemp(string path) => inner.CreateTemp(path);
    public FileIdentity? TryGetIdentity(string path) => inner.TryGetIdentity(path);
    public bool DeleteIfSameObject(string path, UInt128 fileId) => inner.DeleteIfSameObject(path, fileId);
    public bool FlushIfSameObject(string path, UInt128 fileId) => inner.FlushIfSameObject(path, fileId);
    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    private sealed class Corrupting(IVerifyFile inner) : IVerifyFile
    {
        public int Read(Span<byte> buffer, long offset)
        {
            int n = inner.Read(buffer, offset);
            if (offset == 0 && n > 0) buffer[0] ^= 0xFF;
            return n;
        }

        public void Dispose() => inner.Dispose();
    }

    private static void Wait(ConcurrentDictionary<string, ManualResetEventSlim> gates, string path)
    {
        string full = Path.GetFullPath(path);
        foreach (var (folder, gate) in gates)
        {
            if (full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) gate.Wait(TimeSpan.FromSeconds(30));
        }
    }
}
