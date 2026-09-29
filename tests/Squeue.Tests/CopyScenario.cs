using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Tests;

/// A test folder with a journal and one source/destination pair.
public sealed class CopyScenario : IDisposable
{
    private readonly string _dbPath;

    public CopyScenario(string srcName = @"src\a.bin", string destName = @"dest\a.bin")
    {
        _dbPath = Dir.PathOf("state.db");
        Journal = Journal.Open(_dbPath);
        Src = Dir.PathOf(srcName);
        Dest = Dir.PathOf(destName);
    }

    public TestDir Dir { get; } = new();
    public Journal Journal { get; private set; }
    public string Src { get; }
    public string Dest { get; }
    public string DestDir => Path.GetDirectoryName(Dest)!;

    public void WriteSource(byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Src)!);
        File.WriteAllBytes(Src, content);
    }

    public void WriteDest(byte[] content)
    {
        Directory.CreateDirectory(DestDir);
        File.WriteAllBytes(Dest, content);
    }

    public long AddEntry(string? hash = "xxh3", ConflictAction action = ConflictAction.Create, FileIdentity? seenDest = null) =>
        Journal.AddEntry(Journal.CreateJob(hash), Src, Dest, new FileInfo(Src).Length, action, seenDest);

    /// Simulates an app restart: a fresh database connection.
    public void ReopenJournal()
    {
        Journal.Dispose();
        Journal = Journal.Open(_dbPath);
    }

    public string[] TempFiles() => Directory.Exists(DestDir) ? Directory.GetFiles(DestDir, "~tq*.tmp") : [];

    public void Dispose()
    {
        Journal.Dispose();
        Dir.Dispose();
    }
}
