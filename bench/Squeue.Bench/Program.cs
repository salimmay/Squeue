// squeue-bench: compares Squeue's copy engine with Windows' own (CopyFileEx).
//   squeue-bench make <folder> [--big-mb 2048] [--small-count 2000] [--small-kb 300]
//   squeue-bench run <source folder> <destination folder> [--chunk-mb 4] [--depth 4] [--rounds 3]
// A warm-up pass reads the source first, so every engine reads it from memory: this measures the write and verify side.
using System.Diagnostics;
using System.Globalization;
using Squeue.Bench;
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;

if (args.Length >= 2 && args[0] == "make") return Make(args[1], args);
if (args.Length >= 3 && args[0] == "run") return Run(args[1], args[2], args);
Console.WriteLine("Usage:\n  squeue-bench make <folder> [--big-mb N] [--small-count N] [--small-kb N]\n" +
                  "  squeue-bench run <source folder> <destination folder> [--chunk-mb N] [--depth N] [--rounds N]");
return 1;

static int Option(string[] args, string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1], CultureInfo.InvariantCulture) : fallback;
}

static int Make(string folder, string[] args)
{
    int bigMb = Option(args, "--big-mb", 2048), count = Option(args, "--small-count", 2000), kb = Option(args, "--small-kb", 300);
    if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
    {
        Console.WriteLine($"Folder isn't empty: {folder}");
        return 1;
    }
    Directory.CreateDirectory(Path.Combine(folder, "small"));
    var random = new Random(1);
    var chunk = new byte[4 * 1024 * 1024];
    using (var big = File.Create(Path.Combine(folder, "big.bin")))
    {
        for (long written = 0; written < bigMb * 1048576L; written += chunk.Length)
        {
            random.NextBytes(chunk);
            big.Write(chunk, 0, (int)Math.Min(chunk.Length, bigMb * 1048576L - written));
        }
    }
    var small = new byte[kb * 1024];
    for (int i = 1; i <= count; i++)
    {
        random.NextBytes(small);
        File.WriteAllBytes(Path.Combine(folder, "small", $"IMG_{i:0000}.JPG"), small);
    }
    Console.WriteLine($"Made {bigMb} MB big.bin and {count} files of {kb} KB in {folder}");
    return 0;
}

static int Run(string source, string destination, string[] args)
{
    int chunkMb = Option(args, "--chunk-mb", 4), depth = Option(args, "--depth", 4);
    var options = BenchOptions.Create(chunkMb * 1048576, depth);
    string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
    long bytes = files.Sum(f => new FileInfo(f).Length);

    // Warm-up: read everything once so every engine starts from the same cache state.
    var sink = new byte[4 * 1024 * 1024];
    foreach (string f in files) { using var s = File.OpenRead(f); while (s.Read(sink) > 0) { } }

    int rounds = Math.Max(1, Option(args, "--rounds", 3));
    Console.WriteLine($"{files.Length} files, {bytes / 1048576.0:F0} MB, chunk {chunkMb} MB, depth {depth}, {rounds} rounds (median)\n\n");
    var engines = new (string Name, Action<string> Copy)[]
    {
        ("Windows (CopyFileEx, cache only)", dest => { foreach (string f in files) WindowsCopy.Copy(f, Target(f, dest)); }),
        ("Windows + flush each file", dest =>
        {
            foreach (string f in files)
            {
                string target = Target(f, dest);
                WindowsCopy.Copy(f, target);
                using var fs = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                fs.Flush(flushToDisk: true);
            }
        }),
        ("Squeue, no verify", dest => SqueueCopy(null, dest)),
        ("Squeue, verify (xxh3)", dest => SqueueCopy("xxh3", dest)),
    };
    var times = engines.Select(_ => new List<double>()).ToArray();
    for (int r = 0; r < rounds; r++)
        for (int k = 0; k < engines.Length; k++)
        {
            int e = (r + k) % engines.Length;
            times[e].Add(Measure(engines[e].Copy));
        }
    Console.WriteLine("| Engine | Median seconds | Median MB/s | Runs (s) |\n| --- | ---: | ---: | --- |");
    for (int e = 0; e < engines.Length; e++)
    {
        var sorted = times[e].OrderBy(t => t).ToList();
        double median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        string runs = string.Join(", ", times[e].Select(t => t.ToString("F1", CultureInfo.InvariantCulture)));
        Console.WriteLine($"| {engines[e].Name} | {median:F1} | {bytes / 1048576.0 / median:F0} | {runs} |");
    }
    return 0;

    string Target(string file, string dest)
    {
        string target = Path.Combine(dest, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return target;
    }

    void SqueueCopy(string? hash, string dest)
    {
        string db = Path.Combine(Path.GetTempPath(), $"squeue-bench-{Guid.NewGuid():N}.db");
        try
        {
            using var journal = Journal.Open(db);
            var copier = new FileCopier(new WindowsFileSystem(), journal, options);
            long job = journal.CreateJob(hash);
            foreach (string f in files)
            {
                long id = journal.AddEntry(job, f, Target(f, dest), new FileInfo(f).Length, ConflictAction.Create, null);
                var result = copier.CopyEntry(id);
                if (result.Outcome != CopyOutcome.Done) throw new IOException($"{f}: {result.Outcome} {result.Message}");
            }
        }
        finally
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(db + suffix); } catch (IOException) { }
            }
        }
    }

    double Measure(Action<string> copy)
    {
        string dest = Path.Combine(destination, "squeue-bench-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        try
        {
            var watch = Stopwatch.StartNew();
            copy(dest);
            watch.Stop();
            return watch.Elapsed.TotalSeconds;
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }
}

internal static class BenchOptions
{
    public static CopyOptions Create(int chunkSize, int depth) => new() { ChunkSize = chunkSize, PipelineDepth = depth };
}
