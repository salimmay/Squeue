using System.Collections.Concurrent;
using Squeue.Core.FileSystem;

namespace Squeue.Tests.Fakes;

/// Passes everything through, but holds reads under chosen folders until the test opens the gate
/// (at most 30 s, so a failing test can't hang the run).
public sealed class GateFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly ConcurrentDictionary<string, ManualResetEventSlim> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ManualResetEventSlim> _verifies = new(StringComparer.OrdinalIgnoreCase);

    public ManualResetEventSlim HoldSourcesUnder(string folder) => _sources.GetOrAdd(Path.GetFullPath(folder), _ => new(false));
    public ManualResetEventSlim HoldVerificationUnder(string folder) => _verifies.GetOrAdd(Path.GetFullPath(folder), _ => new(false));

    public ISourceFile OpenSource(string path) { Wait(_sources, path); return inner.OpenSource(path); }
    public IVerifyFile OpenForVerify(string path) { Wait(_verifies, path); return inner.OpenForVerify(path); }
    public ITempFile CreateTemp(string path) => inner.CreateTemp(path);
    public FileIdentity? TryGetIdentity(string path) => inner.TryGetIdentity(path);
    public bool DeleteIfSameObject(string path, UInt128 fileId) => inner.DeleteIfSameObject(path, fileId);
    public bool FlushIfSameObject(string path, UInt128 fileId) => inner.FlushIfSameObject(path, fileId);
    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    private static void Wait(ConcurrentDictionary<string, ManualResetEventSlim> gates, string path)
    {
        string full = Path.GetFullPath(path);
        foreach (var (folder, gate) in gates)
        {
            if (full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) gate.Wait(TimeSpan.FromSeconds(30));
        }
    }
}
