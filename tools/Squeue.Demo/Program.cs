// Throwaway demo of the Squeue copy engine. Not part of the product.
// Usage: squeue-demo <source file or folder> <destination folder> [--no-verify]
// Kill it mid-copy (Ctrl+C or close the window), then run it again: it recovers first.
using System.Diagnostics;
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;

if (args.Length < 2)
{
    Console.WriteLine("Usage: squeue-demo <source file or folder> <destination folder> [--no-verify]");
    return 1;
}

string source = Path.GetFullPath(args[0]).TrimEnd('\\');
string destRoot = Path.GetFullPath(args[1]);
string? hash = args.Contains("--no-verify") ? null : "xxh3";

var fs = new WindowsFileSystem();
string dbPath = Path.Combine(Path.GetTempPath(), "squeue-demo", "state.db");
using var journal = Journal.Open(dbPath);
var copier = new FileCopier(fs, journal);

// 1. Recover from any previous run that was killed.
var report = new Reconciler(fs, journal).Run();
if (report.Reset.Count + report.Resumed.Count + report.Failed.Count + report.Unowned.Count > 0)
{
    Console.WriteLine($"Recovered from an interrupted run: {report.Resumed.Count} published copies to finish, " +
                      $"{report.Reset.Count} half-written files cleaned up and restarted, {report.Failed.Count} failed, " +
                      $"{report.Unowned.Count} unknown temp files left alone.");
    foreach (long id in report.Resumed.Concat(report.Reset)) Copy(id);
    Console.WriteLine();
}

// 2. Plan: one entry per file. Existing destinations are replaced (after verification).
bool isFile = File.Exists(source);
if (!isFile && !Directory.Exists(source))
{
    Console.WriteLine($"Source not found: {source}");
    return 1;
}
string[] files = isFile ? [source] : Directory.GetFiles(source, "*", SearchOption.AllDirectories);
string baseDir = Path.GetDirectoryName(source)!;
long job = journal.CreateJob(hash);
var entries = new List<long>();
foreach (string file in files)
{
    string dest = Path.Combine(destRoot, Path.GetRelativePath(baseDir, file));
    var existing = fs.TryGetIdentity(dest);
    var action = existing is null ? ConflictAction.Create : ConflictAction.Replace;
    entries.Add(journal.AddEntry(job, file, dest, new FileInfo(file).Length, action, existing));
}

Console.WriteLine($"Copying {files.Length} file(s) to {destRoot} ({(hash is null ? "no verification" : "verify with xxh3")})");
Console.WriteLine();

// 3. Copy.
var total = Stopwatch.StartNew();
long totalBytes = 0;
int done = 0, failed = 0;
foreach (long id in entries)
{
    if (Copy(id)) { done++; totalBytes += journal.GetEntry(id).SourceVersion?.Size ?? 0; }
    else failed++;
}
total.Stop();

Console.WriteLine();
double seconds = Math.Max(total.Elapsed.TotalSeconds, 0.001);
Console.WriteLine($"Done: {done} copied, {failed} failed, {totalBytes / 1048576.0:F1} MB in {seconds:F1} s " +
                  $"({totalBytes / 1048576.0 / seconds:F0} MB/s including verification)");
return failed == 0 ? 0 : 2;

bool Copy(long id)
{
    var entry = journal.GetEntry(id);
    var watch = Stopwatch.StartNew();
    var result = copier.CopyEntry(id);
    if (result.Outcome == CopyOutcome.RetryNeeded)
    {
        Console.WriteLine($"  retry  {entry.DestPath}  ({result.Message})");
        result = copier.CopyEntry(id);
    }
    watch.Stop();
    entry = journal.GetEntry(id);
    double mb = (entry.SourceVersion?.Size ?? entry.PlannedSize) / 1048576.0;
    string label = result.Outcome switch
    {
        CopyOutcome.Done => entry.DestHash is null ? "copied " : "OK     ",
        CopyOutcome.DestinationChanged => "CHANGED",
        _ => "FAILED ",
    };
    Console.WriteLine($"  {label} {Path.GetFileName(entry.DestPath)}  {mb:F1} MB  {watch.Elapsed.TotalSeconds:F1} s" +
                      (entry.DestHash is null ? "" : $"  xxh3 {entry.DestHash}") +
                      (result.Message is null ? "" : $"  — {result.Message}"));
    return result.Outcome == CopyOutcome.Done;
}
