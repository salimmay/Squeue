# Squeue Speed Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Squeue copy about as fast as Windows' own copy engine without weakening any safety rule, and measure it: reading and writing overlap, verification reads ahead while it hashes, small files cost fewer database commits, and a benchmark compares Squeue with Windows on real drives.

**Architecture:**
- A `ChunkPipeline` in `Squeue.Core/Copy` reads a file on a dedicated reader thread into a small ring of pinned, 4096-aligned buffers (allocated once per `FileCopier`), while the calling thread writes (or hashes) the previous chunk. Files no bigger than one chunk run inline, with no thread. Any exception from either side is rethrown on the caller with its original type, and the reader thread has always finished when `Run` returns.
- `FileCopier.WriteTemp` and `HashUnbuffered` use the pipeline. Everything that touches the journal or reports progress stays on the calling thread (the JobRunner thread in the app).
- The per-file journal commits drop from 9 to 5 by committing together the writes that the protocol already treats as one step.
- `bench/Squeue.Bench` measures Windows `CopyFileEx` against Squeue (verify off and on).

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), xUnit, existing `Squeue.Core`.

**Spec:** Squeue — System Design Spec, Revision 3: https://claude.ai/code/artifact/e58e5773-986c-4724-8466-07c85a154be9 — sections "Technology stack" (Threads: dedicated reader and writer threads with a ring buffer, buffers allocated once) and "Copy protocol, moves and recovery". Plan series item 3 in `docs/superpowers/plans/2026-09-29-squeue-core-copy-protocol.md`.

**Branch:** `feat/speed` from `main` (already created; the plan is its first commit).

## Global Constraints

- Every plan-1 safety invariant still holds: unflushed data never gets the final name (`FlushFileBuffers` before and after the rename); an existing destination is replaced only by a verified file; each journal phase commits before the filesystem change that depends on it; cleanup deletes only objects whose file id matches the journal; uncertainty keeps data.
- The journal is only touched on the thread that calls `FileCopier.CopyEntry`. The reader thread only reads files and hashes. `IProgress.Report` is only called on the calling thread.
- Never call the filesystem inside `Journal.Atomically`.
- Chunk sizes stay positive multiples of 4096; unbuffered verification reads use 4096-aligned buffers.
- `PipelineDepth` (buffers in the ring) is at least 2; default 4. Default chunk size stays 4 MiB.
- All existing tests, including the 68 crash-matrix cases, keep passing unchanged.
- Every commit message ends with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (two `-m` flags). Stage explicit paths; never commit `.superpowers/` or `tools/`.

## Review Focus

1. **A read error on the reader thread** (card pulled mid-file) → `CopyEntry` sees the same `FsException` (same Win32 error) as before, the temp file is removed, and no reader thread is left running. Test in Task 3: `A_reader_error_is_rethrown_with_its_type_after_the_reader_stops`; Task 4: `A_source_read_error_mid_file_still_cleans_up`.
2. **Cancel or pause mid-file** → stops within a chunk or two, temp removed, same outcome as before. Existing `CopyProgressTests` + Task 3 `Cancelling_mid_run_throws_and_stops_the_reader`.
3. **A crash at any file operation** → the crash matrix still passes, with `FaultyFileSystem` now safe to call from two threads. Task 3 makes it thread-safe; Task 4 runs the matrix.
4. **Thousands of small files** → no thread per small file, 5 commits per file. Task 2 `A_new_file_copy_takes_five_journal_commits`; Task 4 `Small_files_are_copied_without_the_reader_thread`.
5. **Buffers reused while still in use** → content is byte-identical for files of many chunks at depth 2. Task 3 `Delivers_every_chunk_in_order_with_exact_content`.

---

## File structure

```
bench/Squeue.Bench/Squeue.Bench.csproj        benchmark console (in the solution, so CI builds it)
bench/Squeue.Bench/Program.cs                 make / run commands, markdown table output
bench/Squeue.Bench/WindowsCopy.cs             CopyFileEx P/Invoke
src/Squeue.Core/State/Journal.cs              + CommitCount
src/Squeue.Core/Copy/FileCopier.cs            fewer commits; pipeline in WriteTemp and HashUnbuffered
src/Squeue.Core/Copy/CopyTypes.cs             + CopyOptions.PipelineDepth
src/Squeue.Core/Copy/ChunkPipeline.cs         reader thread + buffer ring
tests/Squeue.Tests/Fakes/FaultyFileSystem.cs  thread-safe Hit
tests/Squeue.Tests/Copy/ChunkPipelineTests.cs
tests/Squeue.Tests/Copy/FileCopierTests.cs    + commit-count and pipeline tests
docs/superpowers/notes/2026-09-29-speed-benchmarks.md
```

---

### Task 1: Benchmark tool and baseline

**Files:**
- Create: `bench/Squeue.Bench/Squeue.Bench.csproj`, `bench/Squeue.Bench/Program.cs`, `bench/Squeue.Bench/WindowsCopy.cs`
- Create: `docs/superpowers/notes/2026-09-29-speed-benchmarks.md`

**Interfaces:**
- Consumes: `Journal`, `FileCopier`, `CopyOptions`, `WindowsFileSystem`, `ConflictAction`, `CopyOutcome`.
- Produces: `squeue-bench.exe` with `make <folder> [--big-mb N] [--small-count N] [--small-kb N]` and `run <source folder> <destination folder> [--chunk-mb N] [--depth N]`. Task 5 reruns it.

- [ ] **Step 1: Create the project**

```bash
dotnet new console -n Squeue.Bench -o bench/Squeue.Bench -f net10.0
dotnet sln Squeue.slnx add bench/Squeue.Bench/Squeue.Bench.csproj
dotnet add bench/Squeue.Bench/Squeue.Bench.csproj reference src/Squeue.Core/Squeue.Core.csproj
```

Replace `bench/Squeue.Bench/Squeue.Bench.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <AssemblyName>squeue-bench</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Squeue.Core\Squeue.Core.csproj" />
  </ItemGroup>
</Project>
```

(`CopyOptions.PipelineDepth` doesn't exist until Task 4; this task passes only `ChunkSize`. Task 4 adds `--depth` wiring — see its Step 6.)

- [ ] **Step 2: Write the tool**

`bench/Squeue.Bench/WindowsCopy.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Squeue.Bench;

/// Windows' own copy engine, the one Explorer builds on.
internal static partial class WindowsCopy
{
    [LibraryImport("kernel32.dll", EntryPoint = "CopyFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CopyFileEx(string source, string destination, nint progress, nint data, nint cancel, uint flags);

    private const uint COPY_FILE_FAIL_IF_EXISTS = 0x1;

    public static void Copy(string source, string destination)
    {
        if (!CopyFileEx(source, destination, 0, 0, 0, COPY_FILE_FAIL_IF_EXISTS))
            throw new IOException($"CopyFileEx failed for '{source}' (Windows error {Marshal.GetLastPInvokeError()}).");
    }
}
```

`bench/Squeue.Bench/Program.cs`:

```csharp
// squeue-bench: compares Squeue's copy engine with Windows' own (CopyFileEx).
//   squeue-bench make <folder> [--big-mb 2048] [--small-count 2000] [--small-kb 300]
//   squeue-bench run <source folder> <destination folder> [--chunk-mb 4] [--depth 4]
// For fair numbers use a real card or a source bigger than RAM. Every engine reads a warm source here
// (a warm-up pass reads it first), so the comparison measures the write and verify side equally.
using System.Diagnostics;
using System.Globalization;
using Squeue.Bench;
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;

if (args.Length >= 2 && args[0] == "make") return Make(args[1], args);
if (args.Length >= 3 && args[0] == "run") return Run(args[1], args[2], args);
Console.WriteLine("Usage:\n  squeue-bench make <folder> [--big-mb N] [--small-count N] [--small-kb N]\n" +
                  "  squeue-bench run <source folder> <destination folder> [--chunk-mb N] [--depth N]");
return 1;

static int Option(string[] args, string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1], CultureInfo.InvariantCulture) : fallback;
}

static int Make(string folder, string[] args)
{
    int bigMb = Option(args, "--big-mb", 2048), count = Option(args, "--small-count", 2000), kb = Option(args, "--small-kb", 300);
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

    Console.WriteLine($"{files.Length} files, {bytes / 1048576.0:F0} MB, chunk {chunkMb} MB, depth {depth}\n");
    Console.WriteLine("| Engine | Seconds | MB/s |\n| --- | ---: | ---: |");
    Measure("Windows (CopyFileEx)", dest => { foreach (string f in files) WindowsCopy.Copy(f, Target(f, dest)); });
    Measure("Squeue, no verify", dest => SqueueCopy(null, dest));
    Measure("Squeue, verify (xxh3)", dest => SqueueCopy("xxh3", dest));
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

    void Measure(string engine, Action<string> copy)
    {
        string dest = Path.Combine(destination, "squeue-bench-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        try
        {
            var watch = Stopwatch.StartNew();
            copy(dest);
            watch.Stop();
            double seconds = watch.Elapsed.TotalSeconds;
            Console.WriteLine($"| {engine} | {seconds:F1} | {bytes / 1048576.0 / seconds:F0} |");
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }
}
```

Add at the end of `Program.cs`:

```csharp
internal static class BenchOptions
{
    // Task 4 adds PipelineDepth here.
    public static CopyOptions Create(int chunkSize, int depth) => new() { ChunkSize = chunkSize };
}
```

- [ ] **Step 3: Build**

Run: `dotnet build Squeue.slnx`
Expected: 0 warnings, 0 errors. `dotnet test` still passes (233).

- [ ] **Step 4: Record the baseline**

Make a modest data set and measure (sizes chosen to fit this machine's free space; the human reruns on real drives):

```bash
dotnet run --project bench/Squeue.Bench -c Release -- make "E:\squeue-bench-src" --big-mb 1024 --small-count 1000 --small-kb 300
dotnet run --project bench/Squeue.Bench -c Release -- run "E:\squeue-bench-src" "E:\squeue-bench-dst"
```

If `C:` has more than 5 GB free, also run with destination `C:\squeue-bench-dst`. Create `docs/superpowers/notes/2026-09-29-speed-benchmarks.md`:

```markdown
# Speed benchmarks

How to reproduce: see the header of `bench/Squeue.Bench/Program.cs`. Numbers depend on the drives; rerun on your own card and backup drives.

## Baseline (before plan 3), <date>, <machine: CPU, source drive → destination drive>

<paste the table(s) the tool printed>
```

Leave `E:\squeue-bench-src` in place for Task 5; delete `E:\squeue-bench-dst` if it remains.

- [ ] **Step 5: Commit**

```bash
git add Squeue.slnx bench/Squeue.Bench/Squeue.Bench.csproj bench/Squeue.Bench/Program.cs bench/Squeue.Bench/WindowsCopy.cs docs/superpowers/notes/2026-09-29-speed-benchmarks.md
git commit -m "bench: compare Squeue with Windows' copy engine, record the baseline" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Fewer journal commits per file

**Files:**
- Modify: `src/Squeue.Core/State/Journal.cs`, `src/Squeue.Core/Copy/FileCopier.cs`
- Test: `tests/Squeue.Tests/Copy/FileCopierTests.cs`

**Interfaces:**
- Produces: `internal long Journal.CommitCount` — commits made by this connection (each autocommit statement and each `Atomically` block counts once).

- [ ] **Step 1: Write the failing tests**

Add to `FileCopierTests`:

```csharp
    [Fact]
    public void A_new_file_copy_takes_five_journal_commits()
    {
        using var s = new CopyScenario();
        s.WriteSource([1, 2, 3]);
        long id = s.AddEntry();
        long before = s.Journal.CommitCount;

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id).Outcome);

        Assert.Equal(5, s.Journal.CommitCount - before);
    }

    [Fact]
    public void A_verified_replacement_takes_six_journal_commits()
    {
        using var s = new CopyScenario();
        s.WriteSource([1, 2, 3]);
        s.WriteDest([9]);
        long id = s.AddEntry(action: ConflictAction.Replace, seenDest: _fs.TryGetIdentity(s.Dest));
        long before = s.Journal.CommitCount;

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id).Outcome);

        Assert.Equal(6, s.Journal.CommitCount - before);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~journal_commits"`
Expected: build FAILS — `CommitCount` doesn't exist.

- [ ] **Step 3: Implement**

In `Journal.cs` add the property next to `_transaction`:

```csharp
    /// Commits made on this connection; each one is a disk sync (synchronous = FULL). Used by tests and benchmarks.
    internal long CommitCount { get; private set; }
```

In `Atomically`, after `_transaction.Commit();` add `CommitCount++;`. In `Exec`, after `cmd.ExecuteNonQuery();` add `if (_transaction is null) CommitCount++;`.

Run the tests: expected FAIL with actual 9 and 11 — confirming today's counts.

In `FileCopier.StartAttempt`, replace

```csharp
            _journal.RecordSourceVersion(entry.Id, source.Identity);
            try { _fs.CreateDirectory(entry.DestDirectory); }
```

with

```csharp
            try { _fs.CreateDirectory(entry.DestDirectory); }
```

and replace

```csharp
            long attemptId = _journal.BeginAttempt(entry.Id, tempName, replaces); // intent is durable before the file exists
            _journal.ClearHashes(entry.Id); // hashes from an earlier attempt must never satisfy this one
            _journal.SetEntryState(entry.Id, EntryState.Active);
```

with

```csharp
            long attemptId = 0;
            // One commit for everything that must be durable before the temp file exists.
            _journal.Atomically(() =>
            {
                _journal.RecordSourceVersion(entry.Id, source.Identity);
                attemptId = _journal.BeginAttempt(entry.Id, tempName, replaces);
                _journal.ClearHashes(entry.Id); // hashes from an earlier attempt must never satisfy this one
                _journal.SetEntryState(entry.Id, EntryState.Active);
            });
```

Replace

```csharp
                    _journal.SetHashes(entry.Id, srcHash, null);
                    _journal.SetPhase(attemptId, AttemptPhase.Written);
```

with

```csharp
                    _journal.Atomically(() =>
                    {
                        _journal.SetHashes(entry.Id, srcHash, null);
                        _journal.SetPhase(attemptId, AttemptPhase.Written);
                    });
```

and replace

```csharp
                            _journal.SetHashes(entry.Id, null, tempHash);
                            _journal.SetPhase(attemptId, AttemptPhase.VerifiedTemp);
```

with

```csharp
                            _journal.Atomically(() =>
                            {
                                _journal.SetHashes(entry.Id, null, tempHash);
                                _journal.SetPhase(attemptId, AttemptPhase.VerifiedTemp);
                            });
```

(Each group sits between the same two filesystem operations as before, so crash ordering is unchanged.)

- [ ] **Step 4: Run the tests**

Run: `dotnet test`
Expected: PASS — all tests, 2 new, crash matrix included.

- [ ] **Step 5: Commit**

```bash
git add src/Squeue.Core/State/Journal.cs src/Squeue.Core/Copy/FileCopier.cs tests/Squeue.Tests/Copy/FileCopierTests.cs
git commit -m "perf: five journal commits per file instead of nine" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Chunk pipeline

**Files:**
- Create: `src/Squeue.Core/Copy/ChunkPipeline.cs`
- Modify: `tests/Squeue.Tests/Fakes/FaultyFileSystem.cs` (thread-safe `Hit`)
- Test: `tests/Squeue.Tests/Copy/ChunkPipelineTests.cs`

**Interfaces:**
- Produces (namespace `Squeue.Core.Copy`, internal):
  - `delegate int ReadChunk(Span<byte> buffer, long offset);`
  - `delegate void ChunkAction(ReadOnlySpan<byte> chunk);`
  - `delegate void ChunkConsumer(ReadOnlySpan<byte> chunk, long offset);`
  - `sealed class ChunkPipeline(int chunkSize, int depth)` with `int ChunkSize`, `int Depth`, `long Run(ReadChunk read, ChunkAction? onRead, ChunkConsumer consume, bool stopOnShortRead, bool overlap, CancellationToken cancellationToken)` — returns the total bytes consumed. Not for concurrent use.

- [ ] **Step 1: Write the failing tests**

`tests/Squeue.Tests/Copy/ChunkPipelineTests.cs`:

```csharp
using Squeue.Core.Copy;

namespace Squeue.Tests.Copy;

public class ChunkPipelineTests
{
    private const int Chunk = 4096;

    private static ReadChunk ReaderOver(byte[] data, Action<long>? beforeRead = null) => (buffer, offset) =>
    {
        beforeRead?.Invoke(offset);
        int n = (int)Math.Min(buffer.Length, data.Length - offset);
        data.AsSpan((int)offset, n).CopyTo(buffer);
        return n;
    };

    [Theory]
    [InlineData(0, 2, true)]
    [InlineData(1, 2, true)]
    [InlineData(Chunk - 1, 4, true)]
    [InlineData(Chunk, 2, true)]
    [InlineData(Chunk + 1, 2, true)]
    [InlineData(10 * Chunk + 7, 2, true)]
    [InlineData(10 * Chunk + 7, 4, true)]
    [InlineData(10 * Chunk + 7, 2, false)]
    public void Delivers_every_chunk_in_order_with_exact_content(int size, int depth, bool overlap)
    {
        var data = TestDir.RandomBytes(size);
        var output = new MemoryStream();
        long expectedOffset = 0;

        long total = new ChunkPipeline(Chunk, depth).Run(ReaderOver(data), null, (chunk, offset) =>
        {
            Assert.Equal(expectedOffset, offset);
            output.Write(chunk);
            expectedOffset += chunk.Length;
        }, stopOnShortRead: false, overlap, CancellationToken.None);

        Assert.Equal(size, total);
        Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public void OnRead_sees_every_chunk_in_order()
    {
        var data = TestDir.RandomBytes(7 * Chunk + 3);
        var hasher = Hashers.Create("xxh3");

        new ChunkPipeline(Chunk, 2).Run(ReaderOver(data), chunk => hasher.Append(chunk), (_, _) => { },
            stopOnShortRead: false, overlap: true, CancellationToken.None);

        var expected = Hashers.Create("xxh3");
        expected.Append(data);
        Assert.Equal(expected.FinishHex(), hasher.FinishHex());
    }

    [Fact]
    public void StopOnShortRead_ends_at_the_first_short_read()
    {
        var data = TestDir.RandomBytes(3 * Chunk + 10);
        int reads = 0;

        long total = new ChunkPipeline(Chunk, 2).Run(ReaderOver(data, _ => reads++), null, (_, _) => { },
            stopOnShortRead: true, overlap: true, CancellationToken.None);

        Assert.Equal(data.Length, total);
        Assert.Equal(4, reads);
    }

    [Fact]
    public void A_reader_error_is_rethrown_with_its_type_after_the_reader_stops()
    {
        var data = TestDir.RandomBytes(10 * Chunk);
        var consumed = new List<long>();
        int readerThread = 0;

        var ex = Assert.Throws<IOException>(() => new ChunkPipeline(Chunk, 2).Run(
            ReaderOver(data, offset =>
            {
                readerThread = Environment.CurrentManagedThreadId;
                if (offset == 3 * Chunk) throw new IOException("card removed");
            }),
            null, (_, offset) => consumed.Add(offset), stopOnShortRead: false, overlap: true, CancellationToken.None));

        Assert.Equal("card removed", ex.Message);
        Assert.Equal(new long[] { 0, Chunk, 2 * Chunk }, consumed);
        Assert.NotEqual(Environment.CurrentManagedThreadId, readerThread);
    }

    [Fact]
    public void A_consumer_error_stops_the_reader_and_is_rethrown()
    {
        var data = TestDir.RandomBytes(100 * Chunk);
        int reads = 0;

        Assert.Throws<InvalidOperationException>(() => new ChunkPipeline(Chunk, 2).Run(
            ReaderOver(data, _ => Interlocked.Increment(ref reads)), null,
            (_, offset) => { if (offset == Chunk) throw new InvalidOperationException("disk full"); },
            stopOnShortRead: false, overlap: true, CancellationToken.None));

        int after = Volatile.Read(ref reads);
        Assert.True(after <= 5, $"The reader kept going after the consumer failed ({after} reads).");
    }

    [Fact]
    public void Cancelling_mid_run_throws_and_stops_the_reader()
    {
        var data = TestDir.RandomBytes(100 * Chunk);
        using var stop = new CancellationTokenSource();
        int reads = 0;

        Assert.ThrowsAny<OperationCanceledException>(() => new ChunkPipeline(Chunk, 2).Run(
            ReaderOver(data, _ => Interlocked.Increment(ref reads)), null,
            (_, offset) => { if (offset == Chunk) stop.Cancel(); },
            stopOnShortRead: false, overlap: true, stop.Token));

        Assert.True(Volatile.Read(ref reads) <= 5);
    }

    [Fact]
    public void The_pipeline_can_be_run_again_after_a_failure()
    {
        var pipeline = new ChunkPipeline(Chunk, 2);
        var data = TestDir.RandomBytes(5 * Chunk);
        Assert.Throws<IOException>(() => pipeline.Run(ReaderOver(data, o => { if (o == Chunk) throw new IOException("x"); }),
            null, (_, _) => { }, false, true, CancellationToken.None));

        var output = new MemoryStream();
        pipeline.Run(ReaderOver(data), null, (chunk, _) => output.Write(chunk), false, true, CancellationToken.None);

        Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public void Rejects_bad_sizes()
    {
        Assert.Throws<ArgumentException>(() => new ChunkPipeline(1000, 2));
        Assert.Throws<ArgumentException>(() => new ChunkPipeline(Chunk, 1));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ChunkPipelineTests`
Expected: build FAILS — `ChunkPipeline` doesn't exist.

- [ ] **Step 3: Implement**

`src/Squeue.Core/Copy/ChunkPipeline.cs`:

```csharp
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Squeue.Core.FileSystem;

namespace Squeue.Core.Copy;

internal delegate int ReadChunk(Span<byte> buffer, long offset);
internal delegate void ChunkAction(ReadOnlySpan<byte> chunk);
internal delegate void ChunkConsumer(ReadOnlySpan<byte> chunk, long offset);

/// Reads a file on a dedicated reader thread while the calling thread consumes earlier chunks, so reading
/// overlaps writing or hashing. The buffers are pinned, 4096-aligned (unbuffered reads need that) and
/// allocated once, then reused for every run. Not for concurrent use.
internal sealed class ChunkPipeline
{
    private readonly Memory<byte>[] _buffers;

    public ChunkPipeline(int chunkSize, int depth)
    {
        if (chunkSize <= 0 || chunkSize % AlignedBuffer.Alignment != 0)
            throw new ArgumentException("Chunk size must be a positive multiple of 4096.", nameof(chunkSize));
        if (depth < 2) throw new ArgumentException("The pipeline needs at least two buffers.", nameof(depth));
        ChunkSize = chunkSize;
        Depth = depth;
        _buffers = new Memory<byte>[depth];
        for (int i = 0; i < depth; i++) _buffers[i] = AllocateAligned(chunkSize);
    }

    public int ChunkSize { get; }
    public int Depth { get; }

    /// Reads with <paramref name="read"/> until a zero-length read (or, with <paramref name="stopOnShortRead"/>, the
    /// first read shorter than a chunk) and hands the chunks in order to <paramref name="consume"/> on the calling
    /// thread. <paramref name="onRead"/> runs on the reading thread, in order. With <paramref name="overlap"/> false
    /// everything runs on the calling thread. Exceptions keep their type; the reader thread has always finished
    /// when this returns. Returns the bytes consumed.
    public long Run(ReadChunk read, ChunkAction? onRead, ChunkConsumer consume, bool stopOnShortRead, bool overlap,
        CancellationToken cancellationToken) =>
        overlap
            ? RunOverlapped(read, onRead, consume, stopOnShortRead, cancellationToken)
            : RunInline(read, onRead, consume, stopOnShortRead, cancellationToken);

    private long RunInline(ReadChunk read, ChunkAction? onRead, ChunkConsumer consume, bool stopOnShortRead,
        CancellationToken cancellationToken)
    {
        var buffer = _buffers[0].Span;
        long offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = read(buffer, offset);
            if (n == 0) break;
            onRead?.Invoke(buffer[..n]);
            consume(buffer[..n], offset);
            offset += n;
            if (stopOnShortRead && n < ChunkSize) break;
        }
        return offset;
    }

    private long RunOverlapped(ReadChunk read, ChunkAction? onRead, ChunkConsumer consume, bool stopOnShortRead,
        CancellationToken cancellationToken)
    {
        using var free = new BlockingCollection<int>(Depth);
        using var filled = new BlockingCollection<(int Buffer, int Length, long Offset)>(Depth);
        for (int i = 0; i < Depth; i++) free.Add(i);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ExceptionDispatchInfo? readerError = null;

        var reader = new Thread(() =>
        {
            try
            {
                long offset = 0;
                while (true)
                {
                    int index = free.Take(stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                    var buffer = _buffers[index].Span;
                    int n = read(buffer, offset);
                    if (n == 0) break;
                    onRead?.Invoke(buffer[..n]);
                    filled.Add((index, n, offset), stop.Token);
                    offset += n;
                    if (stopOnShortRead && n < ChunkSize) break;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { readerError = ExceptionDispatchInfo.Capture(ex); }
            finally { filled.CompleteAdding(); }
        }) { IsBackground = true, Name = "Squeue reader" };

        long total = 0;
        reader.Start();
        try
        {
            foreach (var (index, length, offset) in filled.GetConsumingEnumerable(cancellationToken))
            {
                consume(_buffers[index].Span[..length], offset);
                total = offset + length;
                free.Add(index);
            }
        }
        finally
        {
            stop.Cancel(); // releases the reader if this side stopped early
            reader.Join();
        }

        readerError?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
        return total;
    }

    /// Pinned arrays never move, so an aligned slice stays aligned, and the GC frees them with the pipeline.
    private static unsafe Memory<byte> AllocateAligned(int size)
    {
        const int alignment = AlignedBuffer.Alignment;
        byte[] array = GC.AllocateUninitializedArray<byte>(size + alignment, pinned: true);
        fixed (byte* start = array)
        {
            int offset = (int)((alignment - ((nint)start & (alignment - 1))) & (alignment - 1));
            return array.AsMemory(offset, size);
        }
    }
}
```

In `tests/Squeue.Tests/Fakes/FaultyFileSystem.cs`, make `Hit` thread-safe (the reader thread now calls `VerifyRead` hooks while the caller calls others): add a field `private readonly object _lock = new();` and wrap the whole body of `Hit` in `lock (_lock) { ... }` (throwing inside the lock is fine).

- [ ] **Step 4: Run the tests**

Run `dotnet test --filter FullyQualifiedName~ChunkPipelineTests` three times, then `dotnet test`.
Expected: PASS every time — 15 new tests (8 theory cases + 7 facts), no hangs.

- [ ] **Step 5: Commit**

```bash
git add src/Squeue.Core/Copy/ChunkPipeline.cs tests/Squeue.Tests/Copy/ChunkPipelineTests.cs tests/Squeue.Tests/Fakes/FaultyFileSystem.cs
git commit -m "feat: chunk pipeline with a reader thread and reusable aligned buffers" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Use the pipeline for copying and verifying

**Files:**
- Modify: `src/Squeue.Core/Copy/CopyTypes.cs`, `src/Squeue.Core/Copy/FileCopier.cs`, `bench/Squeue.Bench/Program.cs`
- Test: `tests/Squeue.Tests/Copy/FileCopierTests.cs`

**Interfaces:**
- Consumes: `ChunkPipeline` (Task 3).
- Produces: `CopyOptions.PipelineDepth` (int, default 4, must be ≥ 2); `internal int FileCopier.ReaderThreadsStarted` (test-only counter of overlapped runs).

- [ ] **Step 1: Write the failing tests**

Add to `FileCopierTests`:

```csharp
    [Fact]
    public void Small_files_are_copied_without_the_reader_thread()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(Chunk));
        long id = s.AddEntry();
        var copier = new FileCopier(_fs, s.Journal, Options);

        Assert.Equal(CopyOutcome.Done, copier.CopyEntry(id).Outcome);

        Assert.Equal(0, copier.ReaderThreadsStarted);
    }

    [Fact]
    public void Large_files_are_copied_and_verified_through_the_reader_thread()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(20 * Chunk + 5);
        s.WriteSource(content);
        long id = s.AddEntry();
        var copier = new FileCopier(_fs, s.Journal, Options);

        Assert.Equal(CopyOutcome.Done, copier.CopyEntry(id).Outcome);

        Assert.Equal(content, File.ReadAllBytes(s.Dest));
        Assert.Equal(2, copier.ReaderThreadsStarted); // one for the copy, one for the verification
    }

    [Fact]
    public void A_source_read_error_mid_file_still_cleans_up()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(20 * Chunk));
        long id = s.AddEntry();
        var fs = new FailingSourceFileSystem(_fs, failAtOffset: 5 * Chunk);

        var result = new FileCopier(fs, s.Journal, Options).CopyEntry(id);

        Assert.Equal(CopyOutcome.Failed, result.Outcome);
        Assert.Equal(21, result.Win32Error);
        Assert.False(File.Exists(s.Dest));
        Assert.Empty(s.TempFiles());
        Assert.Empty(s.Journal.OpenAttempts());
    }

    [Fact]
    public void Rejects_a_pipeline_depth_below_two() =>
        Assert.Throws<ArgumentException>(() => new FileCopier(_fs, null!, new CopyOptions { PipelineDepth = 1 }));

    /// Passes everything through, but the source's reads fail with "device not ready" (21) at one offset.
    private sealed class FailingSourceFileSystem(IFileSystem inner, long failAtOffset) : IFileSystem
    {
        public ISourceFile OpenSource(string path) => new Source(inner.OpenSource(path), path, failAtOffset);
        public ITempFile CreateTemp(string path) => inner.CreateTemp(path);
        public IVerifyFile OpenForVerify(string path) => inner.OpenForVerify(path);
        public FileIdentity? TryGetIdentity(string path) => inner.TryGetIdentity(path);
        public bool DeleteIfSameObject(string path, UInt128 fileId) => inner.DeleteIfSameObject(path, fileId);
        public bool FlushIfSameObject(string path, UInt128 fileId) => inner.FlushIfSameObject(path, fileId);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        private sealed class Source(ISourceFile inner, string path, long failAt) : ISourceFile
        {
            public FileIdentity Identity => inner.Identity;
            public int Read(Span<byte> buffer, long offset) =>
                offset == failAt ? throw new FsException("read", path, 21) : inner.Read(buffer, offset);
            public void Dispose() => inner.Dispose();
        }
    }
```

(If `IFileSystem` has members this fake doesn't list, add passthroughs for them; the build tells you.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~FileCopierTests`
Expected: build FAILS — `ReaderThreadsStarted` and `PipelineDepth` don't exist.

- [ ] **Step 3: Implement**

In `CopyTypes.cs`, add to `CopyOptions`:

```csharp
    /// Buffers in the read-ahead ring (at least 2). Memory used per copy is ChunkSize × PipelineDepth.
    public int PipelineDepth { get; init; } = 4;
```

In `FileCopier`:

1. Constructor: after the chunk-size check add
```csharp
        if (_options.PipelineDepth < 2)
            throw new ArgumentException("Pipeline depth must be at least 2.", nameof(options));
```
2. Add members:
```csharp
    private ChunkPipeline? _pipeline;

    /// Overlapped (reader-thread) runs so far; tests use it to check small files stay inline.
    internal int ReaderThreadsStarted { get; private set; }

    private long RunPipeline(ReadChunk read, ChunkAction? onRead, ChunkConsumer consume, bool stopOnShortRead,
        long expectedSize, CancellationToken cancellationToken)
    {
        _pipeline ??= new ChunkPipeline(_options.ChunkSize, _options.PipelineDepth);
        // A file that fits in one chunk gains nothing from a second thread.
        bool overlap = expectedSize > _options.ChunkSize;
        if (overlap) ReaderThreadsStarted++;
        return _pipeline.Run(read, onRead, consume, stopOnShortRead, overlap, cancellationToken);
    }
```
3. Replace the loop in `WriteTemp` (from `var buffer = new byte[_options.ChunkSize];` through the end of the `while` loop) with:
```csharp
        // The reader thread reads and hashes ahead while this thread writes; progress stays on this thread.
        long offset = RunPipeline(
            read: (buffer, at) => source.Read(buffer, at),
            onRead: hasher is null ? null : chunk => hasher.Append(chunk),
            consume: (chunk, at) =>
            {
                temp.Write(chunk, at);
                progress?.Report(new CopyProgress(CopyStage.Copying, at + chunk.Length, total));
            },
            stopOnShortRead: false,
            expectedSize: total,
            cancellationToken);
```
   (`temp.SetLength(offset);` and the rest stay as they are.)
4. Replace the body of `HashUnbuffered` after `using var file = _fs.OpenForVerify(path);` with:
```csharp
        // The reader thread fetches the next chunk from disk while this thread hashes the current one.
        RunPipeline(
            read: (buffer, at) => file.Read(buffer, at),
            onRead: null,
            consume: (chunk, at) =>
            {
                hasher.Append(chunk);
                progress?.Report(new CopyProgress(CopyStage.Verifying, at + chunk.Length, total));
            },
            stopOnShortRead: true,
            expectedSize: total,
            cancellationToken);
        return hasher.FinishHex();
```
   and remove the `using var buffer = new AlignedBuffer(...)` line.

5. In `bench/Squeue.Bench/Program.cs`, change `BenchOptions.Create` to
```csharp
    public static CopyOptions Create(int chunkSize, int depth) => new() { ChunkSize = chunkSize, PipelineDepth = depth };
```
   and delete its "Task 4 adds" comment.

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~FileCopierTests|FullyQualifiedName~CrashMatrixTests|FullyQualifiedName~CopyProgressTests|FullyQualifiedName~JobRunnerTests"` three times, then `dotnet test`.
Expected: PASS every time — all tests, 4 new; the crash matrix unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/Squeue.Core/Copy/CopyTypes.cs src/Squeue.Core/Copy/FileCopier.cs bench/Squeue.Bench/Program.cs tests/Squeue.Tests/Copy/FileCopierTests.cs
git commit -m "perf: read ahead on a reader thread while writing and verifying" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Measure again

**Files:**
- Modify: `docs/superpowers/notes/2026-09-29-speed-benchmarks.md`

- [ ] **Step 1: Rerun the benchmark** with exactly the Task 1 commands (same source folder, same destinations), plus one extra run with `--depth 2` and one with `--chunk-mb 8` on the first destination.

- [ ] **Step 2: Record** a new section `## After plan 3, <date>` with the tables, and a short `## Reading the numbers` paragraph: Squeue vs Windows for the big file and for the small files, what verification costs, and whether depth or chunk size changed anything. State plainly if Squeue is still slower somewhere.

- [ ] **Step 3: Clean up** `E:\squeue-bench-src` and any `squeue-bench-*` folders left on the destinations.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/notes/2026-09-29-speed-benchmarks.md
git commit -m "docs: speed after read-ahead and fewer commits" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## What this plan does not cover

| Item | Where |
| --- | --- |
| Unbuffered (cache-bypassing) writes for very large files | Revisit after Task 5's numbers; separate small plan if writes are the bottleneck |
| Several copies at once on different disks | Plan 5 — scheduler |
| Real power-cut testing | Manual runbook (plan 1 follow-ups) |
| Speed shown in the app (moving average instead of whole-run average) | Plan 7 — full app |
