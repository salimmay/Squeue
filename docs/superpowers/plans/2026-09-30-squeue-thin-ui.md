# Squeue Thin UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A first usable Squeue window: drop files or folders, choose a destination, confirm a short plan, and watch job cards copy them. Pause, resume and cancel work, and an unfinished job carries on next launch. It runs on the crash-safe engine from plan 1.

**Architecture:** Three new layers on top of `Squeue.Core`:
- **Engine additions** (Tasks 1–4): progress reporting and cancellation in `FileCopier`, job metadata in the journal, a `JobPlanner` that lists the files to copy, and a `JobRunner` that runs queued jobs one at a time on its own thread and raises snapshots.
- **`Squeue.ViewModels`** (Task 5): a plain class library with the MVVM view models, unit-tested without WPF.
- **`Squeue.App`** (Task 6): the WPF window, in the minimal Apple-like style agreed in design: drive pills, job cards, a plan panel, few animations.

Per-disk scheduling is plan 5, so this version runs one job at a time.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), WPF with the built-in Fluent theme (`ThemeMode="System"`), CommunityToolkit.Mvvm, xUnit, existing `Squeue.Core`.

**Spec:** Squeue — System Design Spec, Revision 3: https://claude.ai/code/artifact/e58e5773-986c-4724-8466-07c85a154be9 — sections "IPC contract and entry points" (commands and events), "Technology stack". UI direction: the minimal queue mock-up agreed in conversation on 2026-09-29 (drive pills, one card list in queue order, one accent-free color, no bounce).

**Builds on:** plan 1, `docs/superpowers/plans/2026-09-29-squeue-core-copy-protocol.md` (PR salimmay/Squeue#1). Work on branch `feat/thin-ui`, created from `feat/core-copy-protocol`.

## Scope

In: single-queue job runner, pause/resume/cancel, restart recovery, plan panel with "replace existing" and "verify" choices, drag-and-drop and pickers, drive pills showing busy drives, job cards with progress, speed and time left, a "done today" line.

Out (later plans): per-disk parallel scheduling and the capacity ledger (plan 5), conflict prompts per file (plan 6: here, existing files are either all skipped or all replaced), moves (plan 2), history view and reports, compact window, shell integration (plan 8), throughput tuning (plan 3).

## Global Constraints

- Windows 10/11 x64; every project targets `net10.0-windows`; nothing requires admin rights.
- The journal is single-threaded: only the `JobRunner`'s thread opens and uses it while the app runs.
- Never call the filesystem inside `Journal.Atomically(...)`.
- Default verification is on with `xxh3`; turning it off stores a null hash algorithm on the job.
- Cancelling or pausing mid-file leaves no temp file and no partial file at a final name.
- Progress events are throttled to at most 4 per second per job (`ProgressInterval` default 250 ms); state changes are published immediately.
- The app's journal lives at `%LOCALAPPDATA%\Squeue\state.db`.
- UI copy follows sentence case, no exclamation marks, no "please"/"successfully".
- Every commit message ends with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (two `-m` flags).

## Review Focus

1. **Pausing or cancelling in the middle of a big file** → the file stops within one chunk, the temp file is removed, nothing appears at the final name, and resume copies it fully. Tests in Task 4: `Pause_stops_mid_file_and_resume_finishes`, `Cancel_stops_mid_file_and_leaves_nothing_behind`.
2. **Closing the app during a copy** → the job continues on the next launch. Test in Task 4: `A_job_left_running_by_a_previous_session_is_finished_on_start`; manual check in Task 6.
3. **A file with the same name appears at the destination while the job runs** → that file is reported failed with a clear message, the other file is kept, and the job still finishes (no endless retry). Test in Task 4: `A_destination_that_appears_mid_job_fails_that_file_and_keeps_it`.
4. **Copying a folder into itself** (destination inside the source) → refused with a clear message before anything is queued. Test in Task 3: `Copying_a_folder_into_itself_is_refused`.
5. **An empty folder** → the job finishes immediately as done at 100 %. Test in Task 4: `An_empty_folder_finishes_immediately`.

---

## File structure

```
src/Squeue.Core/State/Records.cs            + JobState, JobRow, EntryRow
src/Squeue.Core/State/Journal.cs            + job metadata columns, migration, job/entry queries
src/Squeue.Core/Copy/CopyTypes.cs           + CopyStage, CopyProgress, CopyOutcome.Cancelled
src/Squeue.Core/Copy/FileCopier.cs          progress + cancellation (whole file replaced in Task 2)
src/Squeue.Core/Jobs/JobPlanner.cs          PlannedFile, JobPlan, JobPlanner
src/Squeue.Core/Jobs/JobQueue.cs            OverwritePolicy, JobSnapshot, IJobQueue, JobRunnerOptions
src/Squeue.Core/Jobs/JobRunner.cs           the runner
src/Squeue.ViewModels/Squeue.ViewModels.csproj
src/Squeue.ViewModels/Format.cs             bytes, speed, time left, counts
src/Squeue.ViewModels/Drives.cs             IDriveSource, DriveInfoLite, DriveViewModel
src/Squeue.ViewModels/JobCardViewModel.cs
src/Squeue.ViewModels/PlanViewModel.cs
src/Squeue.ViewModels/MainViewModel.cs
src/Squeue.App/Squeue.App.csproj
src/Squeue.App/App.xaml, App.xaml.cs, SystemDrives.cs, MainWindow.xaml, MainWindow.xaml.cs
tests/Squeue.Tests/State/JobJournalTests.cs
tests/Squeue.Tests/Copy/CopyProgressTests.cs
tests/Squeue.Tests/Jobs/JobPlannerTests.cs
tests/Squeue.Tests/Jobs/RunnerHarness.cs
tests/Squeue.Tests/Jobs/JobRunnerTests.cs
tests/Squeue.Tests/ViewModels/FakeJobQueue.cs
tests/Squeue.Tests/ViewModels/ViewModelTests.cs
tests/Squeue.Tests/ViewModels/FormatTests.cs
```

---

### Task 1: Job metadata in the journal

**Files:**
- Modify: `src/Squeue.Core/State/Records.cs`, `src/Squeue.Core/State/Journal.cs`
- Test: `tests/Squeue.Tests/State/JobJournalTests.cs`

**Interfaces:**
- Consumes: existing `Journal` internals (`Exec`, `Insert`, `Command`, `Text`, `Now`).
- Produces (namespace `Squeue.Core.State`):
  - `enum JobState { Queued, Running, Paused, Done, Cancelled }`
  - `sealed record JobRow(long Id, string? Name, string? Source, string? DestRoot, JobState State, string? HashAlgorithm, long CreatedAt)` — `CreatedAt` is FILETIME UTC.
  - `sealed record EntryRow(long Id, string SrcPath, long PlannedSize, EntryState State, string? Error)`
  - `Journal.CreateJob(string? hashAlgorithm, string? name = null, string? source = null, string? destRoot = null)` (existing callers keep working)
  - `void SetJobState(long jobId, JobState state)`, `JobRow GetJob(long jobId)` (throws `KeyNotFoundException`), `IReadOnlyList<JobRow> Jobs()` (by id), `IReadOnlyList<EntryRow> EntriesOf(long jobId)` (by id).

- [ ] **Step 1: Create the branch**

```bash
git checkout feat/core-copy-protocol
git checkout -b feat/thin-ui
```

- [ ] **Step 2: Write the failing tests**

`tests/Squeue.Tests/State/JobJournalTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Squeue.Core.State;

namespace Squeue.Tests.State;

public class JobJournalTests
{
    [Fact]
    public void Jobs_store_name_source_destination_and_state()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));

        long id = journal.CreateJob("xxh3", "DCIM", @"F:\DCIM", @"E:\Backup");
        journal.SetJobState(id, JobState.Paused);

        var job = journal.GetJob(id);
        Assert.Equal(("DCIM", @"F:\DCIM", @"E:\Backup"), (job.Name, job.Source, job.DestRoot));
        Assert.Equal((JobState.Paused, "xxh3"), (job.State, job.HashAlgorithm));
        Assert.True(job.CreatedAt > 0);
        Assert.Equal(new[] { id }, journal.Jobs().Select(j => j.Id));
    }

    [Fact]
    public void New_jobs_start_queued()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        Assert.Equal(JobState.Queued, journal.GetJob(journal.CreateJob(null)).State);
    }

    [Fact]
    public void EntriesOf_lists_a_jobs_entries_in_order()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long job = journal.CreateJob(null);
        long other = journal.CreateJob(null);
        long a = journal.AddEntry(job, "a", "A", 10, ConflictAction.Create, null);
        journal.AddEntry(other, "x", "X", 1, ConflictAction.Create, null);
        long b = journal.AddEntry(job, "b", "B", 20, ConflictAction.Create, null);
        journal.SetEntryState(b, EntryState.Failed, "boom");

        var rows = journal.EntriesOf(job);

        Assert.Equal(new[] { a, b }, rows.Select(r => r.Id));
        Assert.Equal(new EntryRow(b, "b", 20, EntryState.Failed, "boom"), rows[1]);
    }

    [Fact]
    public void GetJob_throws_for_an_unknown_id()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        Assert.Throws<KeyNotFoundException>(() => journal.GetJob(99));
    }

    [Fact]
    public void Opens_a_database_created_before_jobs_had_names()
    {
        using var dir = new TestDir();
        string path = dir.PathOf("state.db");
        using (var old = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE jobs (id INTEGER PRIMARY KEY, hash_algo TEXT, created_at INTEGER NOT NULL);
                INSERT INTO jobs (hash_algo, created_at) VALUES ('xxh3', 1);
                """;
            cmd.ExecuteNonQuery();
        }

        using var journal = Journal.Open(path);

        var legacy = journal.Jobs().Single();
        Assert.Equal((JobState.Queued, (string?)null), (legacy.State, legacy.Name));
        long id = journal.CreateJob(null, "new", "src", "dest");
        Assert.Equal("new", journal.GetJob(id).Name);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~JobJournalTests`
Expected: build FAILS — `JobState`, `SetJobState`, `GetJob` don't exist.

- [ ] **Step 4: Write the implementation**

In `src/Squeue.Core/State/Records.cs`, add after the `AttemptPhase` enum:

```csharp
public enum JobState { Queued, Running, Paused, Done, Cancelled }

public sealed record JobRow(
    long Id,
    string? Name,
    string? Source,
    string? DestRoot,
    JobState State,
    string? HashAlgorithm,
    long CreatedAt);

public sealed record EntryRow(long Id, string SrcPath, long PlannedSize, EntryState State, string? Error);
```

In `src/Squeue.Core/State/Journal.cs`:

1. Replace the `jobs` table in `Schema` with:

```sql
        CREATE TABLE IF NOT EXISTS jobs (
          id         INTEGER PRIMARY KEY,
          hash_algo  TEXT,
          name       TEXT,
          source     TEXT,
          dest_root  TEXT,
          state      TEXT NOT NULL DEFAULT 'Queued',
          created_at INTEGER NOT NULL
        );
```

2. In `Open`, after `journal.Exec(Schema);` add `journal.Migrate();`.

3. Replace `CreateJob` with:

```csharp
    public long CreateJob(string? hashAlgorithm, string? name = null, string? source = null, string? destRoot = null) =>
        Insert("""
            INSERT INTO jobs (hash_algo, name, source, dest_root, state, created_at)
            VALUES ($hash, $name, $source, $dest, 'Queued', $now)
            """,
            ("$hash", hashAlgorithm), ("$name", name), ("$source", source), ("$dest", destRoot), ("$now", Now()));

    public void SetJobState(long jobId, JobState state) =>
        Exec("UPDATE jobs SET state = $s WHERE id = $id", ("$s", state.ToString()), ("$id", jobId));

    public JobRow GetJob(long jobId) =>
        ReadJobs("WHERE id = $id", ("$id", jobId)).SingleOrDefault() ?? throw new KeyNotFoundException($"No job {jobId}.");

    public IReadOnlyList<JobRow> Jobs() => ReadJobs("ORDER BY id");

    public IReadOnlyList<EntryRow> EntriesOf(long jobId)
    {
        using var cmd = Command("SELECT id, src_path, planned_size, state, error FROM entries WHERE job_id = $job ORDER BY id",
            ("$job", jobId));
        using var r = cmd.ExecuteReader();
        var rows = new List<EntryRow>();
        while (r.Read())
            rows.Add(new EntryRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), Enum.Parse<EntryState>(r.GetString(3)), Text(r, 4)));
        return rows;
    }
```

4. Add these private members next to `ReadAttempts`:

```csharp
    private List<JobRow> ReadJobs(string where, params (string Name, object? Value)[] args)
    {
        using var cmd = Command("SELECT id, name, source, dest_root, state, hash_algo, created_at FROM jobs " + where, args);
        using var r = cmd.ExecuteReader();
        var rows = new List<JobRow>();
        while (r.Read())
        {
            rows.Add(new JobRow(r.GetInt64(0), Text(r, 1), Text(r, 2), Text(r, 3),
                Enum.Parse<JobState>(r.GetString(4)), Text(r, 5), r.GetInt64(6)));
        }
        return rows;
    }

    /// Adds columns that databases created by earlier builds don't have yet.
    private void Migrate()
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = Command("PRAGMA table_info(jobs)"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) columns.Add(r.GetString(1));
        }
        if (!columns.Contains("name")) Exec("ALTER TABLE jobs ADD COLUMN name TEXT");
        if (!columns.Contains("source")) Exec("ALTER TABLE jobs ADD COLUMN source TEXT");
        if (!columns.Contains("dest_root")) Exec("ALTER TABLE jobs ADD COLUMN dest_root TEXT");
        if (!columns.Contains("state")) Exec("ALTER TABLE jobs ADD COLUMN state TEXT NOT NULL DEFAULT 'Queued'");
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — all tests, 5 new.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: job name, source, destination and state in the journal" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Progress and cancellation in the copier

**Files:**
- Modify: `src/Squeue.Core/Copy/CopyTypes.cs`, `src/Squeue.Core/Copy/FileCopier.cs` (whole file below)
- Test: `tests/Squeue.Tests/Copy/CopyProgressTests.cs`

**Interfaces:**
- Consumes: everything `FileCopier` already uses.
- Produces:
  - `enum CopyStage { Copying, Verifying }`, `readonly record struct CopyProgress(CopyStage Stage, long BytesDone, long BytesTotal)`
  - `CopyOutcome.Cancelled`
  - `FileCopier.CopyEntry(long entryId, IProgress<CopyProgress>? progress = null, CancellationToken cancellationToken = default)` — reports after every chunk; returns `Cancelled` when the token fires. Cancelled while writing: temp deleted, entry `Pending`, attempt `Abandoned`. Cancelled while verifying a published copy: the attempt stays `Published`, so the next `CopyEntry` verifies and finishes it.

- [ ] **Step 1: Write the failing tests**

`tests/Squeue.Tests/Copy/CopyProgressTests.cs`:

```csharp
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Tests.Copy;

public class CopyProgressTests
{
    private const int Chunk = 64 * 1024;
    private static readonly CopyOptions Options = new() { ChunkSize = Chunk, SharingRetryTimeout = TimeSpan.Zero };
    private readonly WindowsFileSystem _fs = new();

    private sealed class Recorder(Action<CopyProgress>? onReport = null) : IProgress<CopyProgress>
    {
        public List<CopyProgress> Reports { get; } = [];

        public void Report(CopyProgress value)
        {
            Reports.Add(value);
            onReport?.Invoke(value);
        }
    }

    [Fact]
    public void Reports_copy_then_verify_progress_up_to_the_file_size()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(3 * Chunk + 1);
        s.WriteSource(content);
        long id = s.AddEntry();
        var recorder = new Recorder();

        var result = new FileCopier(_fs, s.Journal, Options).CopyEntry(id, recorder);

        Assert.Equal(CopyOutcome.Done, result.Outcome);
        var copying = recorder.Reports.Where(r => r.Stage == CopyStage.Copying).ToList();
        var verifying = recorder.Reports.Where(r => r.Stage == CopyStage.Verifying).ToList();
        Assert.Equal(4, copying.Count);
        Assert.Equal(content.Length, copying[^1].BytesDone);
        Assert.All(recorder.Reports, r => Assert.Equal(content.Length, r.BytesTotal));
        Assert.Equal(content.Length, verifying[^1].BytesDone);
        Assert.True(recorder.Reports.IndexOf(copying[^1]) < recorder.Reports.IndexOf(verifying[0]));
    }

    [Fact]
    public void Cancelling_while_writing_removes_the_temp_and_leaves_the_entry_pending()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(4 * Chunk);
        s.WriteSource(content);
        long id = s.AddEntry();
        using var stop = new CancellationTokenSource();
        var copier = new FileCopier(_fs, s.Journal, Options);

        var result = copier.CopyEntry(id, new Recorder(_ => stop.Cancel()), stop.Token);

        Assert.Equal(CopyOutcome.Cancelled, result.Outcome);
        Assert.False(File.Exists(s.Dest));
        Assert.Empty(s.TempFiles());
        Assert.Equal(EntryState.Pending, s.Journal.GetEntry(id).State);
        Assert.Empty(s.Journal.OpenAttempts());

        Assert.Equal(CopyOutcome.Done, copier.CopyEntry(id).Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
    }

    [Fact]
    public void Cancelling_while_verifying_a_published_copy_finishes_it_next_time()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(2 * Chunk);
        s.WriteSource(content);
        long id = s.AddEntry();
        using var stop = new CancellationTokenSource();
        var copier = new FileCopier(_fs, s.Journal, Options);

        var result = copier.CopyEntry(id, new Recorder(p => { if (p.Stage == CopyStage.Verifying) stop.Cancel(); }), stop.Token);

        Assert.Equal(CopyOutcome.Cancelled, result.Outcome);
        Assert.Equal(AttemptPhase.Published, s.Journal.GetOpenAttempt(id)!.Phase);
        Assert.Equal(CopyOutcome.Done, copier.CopyEntry(id).Outcome);
        Assert.Single(s.Journal.AttemptsFor(id));
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
    }

    [Fact]
    public void An_already_cancelled_token_changes_nothing()
    {
        using var s = new CopyScenario();
        s.WriteSource([1, 2, 3]);
        long id = s.AddEntry();
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var result = new FileCopier(_fs, s.Journal, Options).CopyEntry(id, null, stop.Token);

        Assert.Equal(CopyOutcome.Cancelled, result.Outcome);
        Assert.Empty(s.Journal.AttemptsFor(id));
        Assert.Equal(EntryState.Pending, s.Journal.GetEntry(id).State);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~CopyProgressTests`
Expected: build FAILS — `CopyProgress`, `CopyStage` and `CopyOutcome.Cancelled` don't exist.

- [ ] **Step 3: Write the implementation**

In `src/Squeue.Core/Copy/CopyTypes.cs`, add `Cancelled` as the last `CopyOutcome` member:

```csharp
    /// Verification failed once; the entry is pending and will be copied again.
    RetryNeeded,

    /// Stopped on request. A half-written temp was removed; a published copy is verified when the entry resumes.
    Cancelled,
}
```

and add at the end of the file:

```csharp
public enum CopyStage { Copying, Verifying }

/// Progress within one file. Verifying re-reads the copy, so BytesDone restarts from 0 for that stage.
public readonly record struct CopyProgress(CopyStage Stage, long BytesDone, long BytesTotal);
```

Replace `src/Squeue.Core/Copy/FileCopier.cs` with:

```csharp
using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Core.Copy;

/// Copies one journal entry following the per-file protocol:
/// intent → temp created → written + flushed → (verified, when replacing) → published + flushed → verified → finished.
/// Each phase is committed to the journal before the next filesystem change, so the Reconciler can always
/// tell what happened after a crash.
public sealed class FileCopier
{
    // Read-only, hidden, system, archive, not-content-indexed. Compression, encryption and sparse are not copied.
    private const uint CopiedAttributes = 0x1 | 0x2 | 0x4 | 0x20 | 0x2000;

    private readonly IFileSystem _fs;
    private readonly Journal _journal;
    private readonly CopyOptions _options;

    public FileCopier(IFileSystem fs, Journal journal, CopyOptions? options = null)
    {
        _options = options ?? new CopyOptions();
        if (_options.ChunkSize <= 0 || _options.ChunkSize % AlignedBuffer.Alignment != 0)
            throw new ArgumentException("Chunk size must be a positive multiple of 4096.", nameof(options));
        _fs = fs;
        _journal = journal;
    }

    public CopyResult CopyEntry(long entryId, IProgress<CopyProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var entry = _journal.GetEntry(entryId);
            if (entry.State == EntryState.Done) return new CopyResult(CopyOutcome.Done);

            var open = _journal.GetOpenAttempt(entryId);
            if (open is { Phase: AttemptPhase.Published or AttemptPhase.Verified })
                return FinishPublished(entry, open, progress, cancellationToken);
            if (open is not null)
                throw new InvalidOperationException($"Entry {entryId} has an unreconciled attempt. Run the Reconciler first.");

            cancellationToken.ThrowIfCancellationRequested();
            return StartAttempt(entry, progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // In-flight work is already cleaned up (temp deleted) or left resumable (published, not yet verified).
            return new CopyResult(CopyOutcome.Cancelled, "Stopped on request.");
        }
    }

    private CopyResult StartAttempt(Entry entry, IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        if (entry.HashAlgorithm is { } algorithm && !Hashers.Supported.Contains(algorithm))
            return Fail(entry, $"Unknown hash algorithm '{algorithm}'.");

        ISourceFile source;
        try { source = OpenSourceWithRetry(entry.SrcPath); }
        catch (FsException ex) when (ex.IsSharingViolation) { return Fail(entry, "The source is in use by another program."); }
        catch (FsException ex) when (ex.IsNotFound) { return Fail(entry, "The source no longer exists."); }
        catch (IOException ex) { return Fail(entry, ex.Message); }

        using (source)
        {
            _journal.RecordSourceVersion(entry.Id, source.Identity);
            try { _fs.CreateDirectory(entry.DestDirectory); }
            catch (IOException ex) { return Fail(entry, ex.Message); }
            catch (UnauthorizedAccessException ex) { return Fail(entry, ex.Message); }

            string tempName = TempNames.New();
            string tempPath = Path.Combine(entry.DestDirectory, tempName);
            UInt128? replaces = entry.Action == ConflictAction.Replace ? entry.SeenDest?.FileId : null;
            long attemptId = _journal.BeginAttempt(entry.Id, tempName, replaces); // intent is durable before the file exists
            _journal.ClearHashes(entry.Id); // hashes from an earlier attempt must never satisfy this one
            _journal.SetEntryState(entry.Id, EntryState.Active);

            ITempFile temp;
            try { temp = _fs.CreateTemp(tempPath); }
            catch (IOException ex)
            {
                return Fail(entry, ex.Message, attemptId);
            }

            using (temp)
            {
                UInt128 tempId = temp.GetIdentity().FileId;
                _journal.SetPhase(attemptId, AttemptPhase.TempCreated, tempFileId: tempId);
                long size = source.Identity.Size;
                try
                {
                    string? srcHash = WriteTemp(source, temp, entry.HashAlgorithm, progress, cancellationToken);
                    _journal.SetHashes(entry.Id, srcHash, null);
                    _journal.SetPhase(attemptId, AttemptPhase.Written);
                    source.Dispose(); // fully read; release it before publishing

                    if (entry.Action == ConflictAction.Replace)
                    {
                        // Never replace an existing file with one that hasn't passed verification.
                        if (srcHash is not null)
                        {
                            string tempHash = HashUnbuffered(tempPath, entry.HashAlgorithm!, size, progress, cancellationToken);
                            if (tempHash != srcHash)
                            {
                                Abandon(temp, tempPath, tempId);
                                return VerificationFailed(entry, attemptId, replaceTarget: null);
                            }
                            _journal.SetHashes(entry.Id, null, tempHash);
                            _journal.SetPhase(attemptId, AttemptPhase.VerifiedTemp);
                        }

                        if (_fs.TryGetIdentity(entry.DestPath) is not { } current
                            || entry.SeenDest is not { } seen
                            || !current.IsSameVersion(seen))
                        {
                            return DestinationChanged(entry, temp, tempPath, tempId, attemptId);
                        }
                    }

                    try { temp.RenameTo(entry.DestPath, replaceExisting: entry.Action == ConflictAction.Replace); }
                    catch (FsException ex) when (ex.IsAlreadyExists) { return DestinationChanged(entry, temp, tempPath, tempId, attemptId); }
                }
                catch (OperationCanceledException)
                {
                    // Stopped before publishing: nothing reached the final name, so remove the temp and start over later.
                    Abandon(temp, tempPath, tempId);
                    _journal.Atomically(() =>
                    {
                        _journal.SetEntryState(entry.Id, EntryState.Pending);
                        _journal.SetPhase(attemptId, AttemptPhase.Abandoned);
                    });
                    throw;
                }
                catch (IOException ex)
                {
                    Abandon(temp, tempPath, tempId);
                    return Fail(entry, ex.Message, attemptId);
                }

                // Make the rename durable. A failure here propagates; reconciliation will find the published file.
                temp.Flush();
                // Read after the rename: some filesystems (FAT/exFAT) can change a file's id when it is renamed.
                _journal.SetPhase(attemptId, AttemptPhase.Published, publishedFileId: temp.GetIdentity().FileId);
            }
        }

        return FinishPublished(_journal.GetEntry(entry.Id), _journal.GetOpenAttempt(entry.Id)!, progress, cancellationToken);
    }

    private CopyResult FinishPublished(Entry entry, Attempt attempt, IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        // The file at the destination must still be the one this attempt published, whatever phase we resume from.
        if (_fs.TryGetIdentity(entry.DestPath) is not { } current || current.FileId != attempt.PublishedFileId)
            return Fail(entry, "The destination changed after it was copied. The source was kept.", attempt.Id);

        string? verifiedHash = null;
        bool needsVerification = attempt.Phase == AttemptPhase.Published && entry.HashAlgorithm is not null && entry.DestHash is null;
        if (needsVerification)
        {
            // Cancelling here leaves the attempt Published: the next CopyEntry verifies it again.
            string destHash = HashUnbuffered(entry.DestPath, entry.HashAlgorithm!, current.Size, progress, cancellationToken);
            if (destHash != entry.SrcHash)
            {
                // The outcome and the attempt's close commit together, so a crash can't strand the entry.
                var published = _fs.TryGetIdentity(entry.DestPath);
                if (published is null)
                    return Fail(entry, "The copied file disappeared before it could be verified. The source was kept.", attempt.Id);
                if (published.Value.FileId != attempt.PublishedFileId)
                    return Fail(entry, "The destination changed after it was copied. The source was kept.", attempt.Id);
                return VerificationFailed(entry, attempt.Id, replaceTarget: published.Value);
            }
            verifiedHash = destHash;
        }

        _journal.Atomically(() =>
        {
            if (verifiedHash is not null) _journal.SetHashes(entry.Id, null, verifiedHash);
            _journal.SetPhase(attempt.Id, AttemptPhase.Finished);
            _journal.SetEntryState(entry.Id, EntryState.Done);
        });
        return new CopyResult(CopyOutcome.Done);
    }

    private string? WriteTemp(ISourceFile source, ITempFile temp, string? algorithm,
        IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        var hasher = algorithm is null ? null : Hashers.Create(algorithm);
        long total = source.Identity.Size;
        temp.Preallocate(total);

        var buffer = new byte[_options.ChunkSize];
        long offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = source.Read(buffer, offset);
            if (read == 0) break;
            var chunk = buffer.AsSpan(0, read);
            hasher?.Append(chunk);
            temp.Write(chunk, offset);
            offset += read;
            progress?.Report(new CopyProgress(CopyStage.Copying, offset, total));
        }

        temp.SetLength(offset);
        var s = source.Identity;
        temp.SetTimesAndAttributes(s.CreationTime, s.LastWriteTime, s.Attributes & CopiedAttributes);
        temp.Flush(); // data is on disk before the file can get its final name
        return hasher?.FinishHex();
    }

    private string HashUnbuffered(string path, string algorithm, long total,
        IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        var hasher = Hashers.Create(algorithm);
        using var file = _fs.OpenForVerify(path);
        using var buffer = new AlignedBuffer(_options.ChunkSize);
        long offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = file.Read(buffer.Span, offset);
            if (read == 0) break;
            hasher.Append(buffer.Span[..read]);
            offset += read;
            progress?.Report(new CopyProgress(CopyStage.Verifying, offset, total));
            if (read < buffer.Length) break;
        }
        return hasher.FinishHex();
    }

    private ISourceFile OpenSourceWithRetry(string path)
    {
        var deadline = DateTime.UtcNow + _options.SharingRetryTimeout;
        while (true)
        {
            try { return _fs.OpenSource(path); }
            catch (FsException ex) when (ex.IsSharingViolation && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(_options.SharingRetryDelay);
            }
        }
    }

    /// Closes our handle and deletes the temp file. The caller records the outcome and closes the attempt.
    private void Abandon(ITempFile temp, string tempPath, UInt128 tempId)
    {
        temp.Dispose(); // close our handle so the delete can open the file
        _fs.DeleteIfSameObject(tempPath, tempId);
    }

    private CopyResult DestinationChanged(Entry entry, ITempFile temp, string tempPath, UInt128 tempId, long attemptId)
    {
        Abandon(temp, tempPath, tempId);
        _journal.Atomically(() =>
        {
            _journal.SetEntryState(entry.Id, EntryState.Pending);
            _journal.SetPhase(attemptId, AttemptPhase.Abandoned);
        });
        return new CopyResult(CopyOutcome.DestinationChanged, "The destination changed after the conflict decision.");
    }

    private CopyResult VerificationFailed(Entry entry, long attemptId, FileIdentity? replaceTarget)
    {
        CopyResult? result = null;
        _journal.Atomically(() =>
        {
            int failures = _journal.RecordVerifyFailure(entry.Id, replaceTarget);
            if (failures >= 2)
            {
                result = Fail(entry, "Verification failed twice. The copy at the destination may be damaged; the source was kept.", attemptId);
                return;
            }
            _journal.SetEntryState(entry.Id, EntryState.Pending);
            _journal.SetPhase(attemptId, AttemptPhase.Abandoned);
            result = new CopyResult(CopyOutcome.RetryNeeded, "Verification failed. The file will be copied again.");
        });
        return result!;
    }

    /// Fails the entry; when an attempt is given, it is closed in the same transaction.
    private CopyResult Fail(Entry entry, string message, long? attemptId = null)
    {
        _journal.Atomically(() =>
        {
            _journal.SetEntryState(entry.Id, EntryState.Failed, message);
            if (attemptId is { } id) _journal.SetPhase(id, AttemptPhase.Abandoned);
        });
        return new CopyResult(CopyOutcome.Failed, message);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — all tests (including all crash-matrix cases), 4 new.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: copy progress and cancellation" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Job planner

**Files:**
- Create: `src/Squeue.Core/Jobs/JobPlanner.cs`
- Test: `tests/Squeue.Tests/Jobs/JobPlannerTests.cs`

**Interfaces:**
- Consumes: `IFileSystem.TryGetIdentity`, `FileIdentity`.
- Produces (namespace `Squeue.Core.Jobs`):
  - `sealed record PlannedFile(string SourcePath, string DestPath, long Size, FileIdentity? ExistingDest)`
  - `sealed record JobPlan(string Name, string Source, string DestRoot, IReadOnlyList<PlannedFile> Files)` with `long TotalBytes`, `int ExistingCount`
  - `static class JobPlanner { static JobPlan Plan(IFileSystem fs, IReadOnlyList<string> sources, string destRoot); }` — a folder keeps its own name under the destination; reparse points are skipped; throws `ArgumentException` (no sources, or a folder copied into itself) or `FileNotFoundException` (missing source).

- [ ] **Step 1: Write the failing tests**

`tests/Squeue.Tests/Jobs/JobPlannerTests.cs`:

```csharp
using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;

namespace Squeue.Tests.Jobs;

public class JobPlannerTests
{
    private readonly WindowsFileSystem _fs = new();

    [Fact]
    public void A_single_file_goes_straight_into_the_destination()
    {
        using var dir = new TestDir();
        string src = dir.Write(@"card\IMG_0001.CR3", new byte[100]);

        var plan = JobPlanner.Plan(_fs, [src], dir.PathOf("backup"));

        var file = Assert.Single(plan.Files);
        Assert.Equal(dir.PathOf(@"backup\IMG_0001.CR3"), file.DestPath);
        Assert.Equal(100, file.Size);
        Assert.Equal(("IMG_0001.CR3", src, dir.PathOf("backup")), (plan.Name, plan.Source, plan.DestRoot));
    }

    [Fact]
    public void A_folder_keeps_its_name_and_structure()
    {
        using var dir = new TestDir();
        dir.Write(@"card\DCIM\a.jpg", new byte[10]);
        dir.Write(@"card\DCIM\100CANON\b.cr3", new byte[20]);

        var plan = JobPlanner.Plan(_fs, [dir.PathOf(@"card\DCIM")], dir.PathOf("backup"));

        Assert.Equal("DCIM", plan.Name);
        Assert.Equal(
            new[] { dir.PathOf(@"backup\DCIM\100CANON\b.cr3"), dir.PathOf(@"backup\DCIM\a.jpg") },
            plan.Files.Select(f => f.DestPath).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(30, plan.TotalBytes);
    }

    [Fact]
    public void Several_sources_are_named_by_count()
    {
        using var dir = new TestDir();
        string a = dir.Write(@"card\a.jpg", new byte[1]);
        string b = dir.Write(@"card\b.jpg", new byte[1]);

        var plan = JobPlanner.Plan(_fs, [a, b], dir.PathOf("backup"));

        Assert.Equal(("2 items", dir.PathOf("card")), (plan.Name, plan.Source));
        Assert.Equal(2, plan.Files.Count);
    }

    [Fact]
    public void Existing_destination_files_are_detected()
    {
        using var dir = new TestDir();
        string src = dir.Write(@"card\a.jpg", new byte[1]);
        string existing = dir.Write(@"backup\a.jpg", new byte[5]);

        var plan = JobPlanner.Plan(_fs, [src], dir.PathOf("backup"));

        Assert.Equal(1, plan.ExistingCount);
        Assert.Equal(_fs.TryGetIdentity(existing), plan.Files[0].ExistingDest);
    }

    [Fact]
    public void Copying_a_folder_into_itself_is_refused()
    {
        using var dir = new TestDir();
        dir.Write(@"card\a.jpg", new byte[1]);

        var ex = Assert.Throws<ArgumentException>(() => JobPlanner.Plan(_fs, [dir.PathOf("card")], dir.PathOf(@"card\copy")));

        Assert.Contains("into itself", ex.Message);
    }

    [Fact]
    public void A_missing_source_is_refused()
    {
        using var dir = new TestDir();
        Assert.Throws<FileNotFoundException>(() => JobPlanner.Plan(_fs, [dir.PathOf("nope")], dir.PathOf("backup")));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~JobPlannerTests`
Expected: build FAILS — `Squeue.Core.Jobs` doesn't exist.

- [ ] **Step 3: Write the implementation**

`src/Squeue.Core/Jobs/JobPlanner.cs`:

```csharp
using Squeue.Core.FileSystem;

namespace Squeue.Core.Jobs;

public sealed record PlannedFile(string SourcePath, string DestPath, long Size, FileIdentity? ExistingDest);

public sealed record JobPlan(string Name, string Source, string DestRoot, IReadOnlyList<PlannedFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.Size);
    public int ExistingCount => Files.Count(f => f.ExistingDest is not null);
}

/// Turns what the user picked into the list of files to copy.
public static class JobPlanner
{
    // Links and junctions are not followed: they could loop or reach outside what the user picked.
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    /// A folder keeps its own name under the destination: F:\DCIM → E:\Backup\DCIM\...
    public static JobPlan Plan(IFileSystem fs, IReadOnlyList<string> sources, string destRoot)
    {
        if (sources.Count == 0) throw new ArgumentException("Choose at least one file or folder to copy.", nameof(sources));
        string dest = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destRoot));
        var files = new List<PlannedFile>();

        foreach (string raw in sources)
        {
            string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            if (File.Exists(source))
            {
                Add(source, Path.Combine(dest, Path.GetFileName(source)));
                continue;
            }
            if (!Directory.Exists(source)) throw new FileNotFoundException($"'{source}' doesn't exist.", source);
            if (IsSameOrInside(dest, source)) throw new ArgumentException($"Can't copy '{source}' into itself.", nameof(destRoot));

            string target = Path.Combine(dest, FolderName(source));
            foreach (string file in Directory.EnumerateFiles(source, "*", Walk).Order(StringComparer.OrdinalIgnoreCase))
                Add(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }

        string first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sources[0]));
        string name = sources.Count == 1 ? FolderName(first) : $"{sources.Count} items";
        string sourceLabel = sources.Count == 1 ? first : Path.GetDirectoryName(first) ?? first;
        return new JobPlan(name, sourceLabel, dest, files);

        void Add(string src, string dst) => files.Add(new PlannedFile(src, dst, new FileInfo(src).Length, fs.TryGetIdentity(dst)));
    }

    // A drive root such as "F:\" has no folder name; use "Drive F" instead.
    private static string FolderName(string path) =>
        Path.GetFileName(path) is { Length: > 0 } name ? name : $"Drive {path[0]}";

    private static bool IsSameOrInside(string path, string folder)
    {
        string f = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        string p = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — all tests, 6 new.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: job planner" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Job runner

**Files:**
- Create: `src/Squeue.Core/Jobs/JobQueue.cs`, `src/Squeue.Core/Jobs/JobRunner.cs`
- Create: `tests/Squeue.Tests/Jobs/RunnerHarness.cs`
- Test: `tests/Squeue.Tests/Jobs/JobRunnerTests.cs`

**Interfaces:**
- Consumes: `Journal` job API (Task 1), `FileCopier` with progress/cancellation (Task 2), `JobPlan` (Task 3), `Reconciler`.
- Produces (namespace `Squeue.Core.Jobs`):
  - `enum OverwritePolicy { Replace, Skip }`
  - `sealed record JobSnapshot(long Id, string Name, string Source, string DestRoot, JobState State, int TotalFiles, int DoneFiles, int FailedFiles, long TotalBytes, long DoneBytes, string? CurrentFile, double BytesPerSecond, string? LastError, DateTime CreatedAtUtc)` with `double Fraction`. `DoneBytes` counts finished files (done or failed) plus the current file's copied bytes.
  - `interface IJobQueue : IDisposable { event Action<JobSnapshot>? JobChanged; void Start(); void Enqueue(JobPlan plan, OverwritePolicy policy, bool verify); void Pause(long jobId); void Resume(long jobId); void Cancel(long jobId); }` — commands may be called from any thread; `JobChanged` is raised on the runner's thread.
  - `sealed record JobRunnerOptions { CopyOptions Copy; TimeSpan ProgressInterval = 250 ms; }`
  - `sealed class JobRunner(IFileSystem fs, string journalPath, JobRunnerOptions? options = null) : IJobQueue`

- [ ] **Step 1: Write the test harness**

`tests/Squeue.Tests/Jobs/RunnerHarness.cs`:

```csharp
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;

namespace Squeue.Tests.Jobs;

/// A JobRunner on a temp folder that records every snapshot and lets tests wait for one.
public sealed class RunnerHarness : IDisposable
{
    public static readonly JobRunnerOptions Options = new()
    {
        Copy = new CopyOptions { ChunkSize = 64 * 1024, SharingRetryTimeout = TimeSpan.Zero },
        ProgressInterval = TimeSpan.Zero,
    };

    private readonly List<JobSnapshot> _events = [];
    private readonly object _lock = new();

    public RunnerHarness(IFileSystem? fs = null)
    {
        Fs = fs ?? new WindowsFileSystem();
        Runner = NewRunner();
    }

    public TestDir Dir { get; } = new();
    public IFileSystem Fs { get; }
    public JobRunner Runner { get; private set; }
    public string JournalPath => Dir.PathOf("state.db");

    /// Called on the runner's thread after each snapshot is recorded.
    public Action<JobSnapshot>? OnEvent { get; set; }

    public JobSnapshot WaitFor(Func<JobSnapshot, bool> condition, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        lock (_lock)
        {
            int seen = 0;
            while (true)
            {
                for (; seen < _events.Count; seen++)
                {
                    if (condition(_events[seen])) return _events[seen];
                }
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                    throw new TimeoutException($"The expected job event never arrived. Last event: {(_events.Count > 0 ? _events[^1] : null)}");
                Monitor.Wait(_lock, left);
            }
        }
    }

    public void Dispose()
    {
        Runner.Dispose();
        Dir.Dispose();
    }

    private JobRunner NewRunner()
    {
        var runner = new JobRunner(Fs, JournalPath, Options);
        runner.JobChanged += snapshot =>
        {
            lock (_lock)
            {
                _events.Add(snapshot);
                Monitor.PulseAll(_lock);
            }
            OnEvent?.Invoke(snapshot);
        };
        return runner;
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Squeue.Tests/Jobs/JobRunnerTests.cs`:

```csharp
using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;
using Squeue.Core.State;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Jobs;

public class JobRunnerTests
{
    private static JobPlan PlanCard(RunnerHarness h, params (string Name, byte[] Content)[] files)
    {
        Directory.CreateDirectory(h.Dir.PathOf("card"));
        foreach (var (name, content) in files) h.Dir.Write(Path.Combine("card", name), content);
        return JobPlanner.Plan(h.Fs, [h.Dir.PathOf("card")], h.Dir.PathOf("backup"));
    }

    private static string[] TempFilesUnder(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "~tq*.tmp", SearchOption.AllDirectories) : [];

    [Fact]
    public void Runs_a_queued_job_to_done()
    {
        using var h = new RunnerHarness();
        var a = TestDir.RandomBytes(100_000, 1);
        var b = TestDir.RandomBytes(5000, 2);
        var plan = PlanCard(h, ("a.bin", a), (@"sub\b.bin", b));
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var done = h.WaitFor(s => s.State == JobState.Done);

        Assert.Equal(("card", 2, 2, 0), (done.Name, done.TotalFiles, done.DoneFiles, done.FailedFiles));
        Assert.Equal(done.TotalBytes, done.DoneBytes);
        Assert.Equal(1.0, done.Fraction);
        Assert.Equal(a, File.ReadAllBytes(h.Dir.PathOf(@"backup\card\a.bin")));
        Assert.Equal(b, File.ReadAllBytes(h.Dir.PathOf(@"backup\card\sub\b.bin")));
    }

    [Fact]
    public void Skip_leaves_existing_files_alone()
    {
        using var h = new RunnerHarness();
        h.Dir.Write(@"backup\card\a.bin", [9]);
        var plan = PlanCard(h, ("a.bin", TestDir.RandomBytes(100)), ("b.bin", TestDir.RandomBytes(100)));
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Skip, verify: true);
        var done = h.WaitFor(s => s.State == JobState.Done);

        Assert.Equal(1, done.TotalFiles);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(h.Dir.PathOf(@"backup\card\a.bin")));
        Assert.True(File.Exists(h.Dir.PathOf(@"backup\card\b.bin")));
    }

    [Fact]
    public void Pause_stops_mid_file_and_resume_finishes()
    {
        using var h = new RunnerHarness();
        var content = TestDir.RandomBytes(8 * 1024 * 1024);
        var plan = PlanCard(h, ("big.bin", content));
        int paused = 0;
        h.OnEvent = s =>
        {
            if (s.State == JobState.Running && s.DoneBytes > 0 && Interlocked.Exchange(ref paused, 1) == 0) h.Runner.Pause(s.Id);
        };
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var p = h.WaitFor(s => s.State == JobState.Paused);

        string dest = h.Dir.PathOf(@"backup\card\big.bin");
        Assert.False(File.Exists(dest));
        Assert.Empty(TempFilesUnder(h.Dir.PathOf("backup")));

        h.OnEvent = null;
        h.Runner.Resume(p.Id);
        h.WaitFor(s => s.Id == p.Id && s.State == JobState.Done);
        Assert.Equal(content, File.ReadAllBytes(dest));
    }

    [Fact]
    public void Cancel_stops_mid_file_and_leaves_nothing_behind()
    {
        using var h = new RunnerHarness();
        var plan = PlanCard(h, ("big.bin", TestDir.RandomBytes(8 * 1024 * 1024)));
        int cancelled = 0;
        h.OnEvent = s =>
        {
            if (s.State == JobState.Running && s.DoneBytes > 0 && Interlocked.Exchange(ref cancelled, 1) == 0) h.Runner.Cancel(s.Id);
        };
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        h.WaitFor(s => s.State == JobState.Cancelled);

        Assert.False(File.Exists(h.Dir.PathOf(@"backup\card\big.bin")));
        Assert.Empty(TempFilesUnder(h.Dir.PathOf("backup")));
    }

    [Fact]
    public void A_job_left_running_by_a_previous_session_is_finished_on_start()
    {
        using var h = new RunnerHarness();
        var content = TestDir.RandomBytes(10_000);
        string src = h.Dir.Write(@"card\a.bin", content);
        string dest = h.Dir.PathOf(@"backup\a.bin");
        long jobId;
        using (var journal = Journal.Open(h.JournalPath))
        {
            jobId = journal.CreateJob("xxh3", "card", h.Dir.PathOf("card"), h.Dir.PathOf("backup"));
            journal.AddEntry(jobId, src, dest, content.Length, ConflictAction.Create, null);
            journal.SetJobState(jobId, JobState.Running);
        }

        h.Runner.Start();
        h.WaitFor(s => s.Id == jobId && s.State == JobState.Done);

        Assert.Equal(content, File.ReadAllBytes(dest));
    }

    [Fact]
    public void A_destination_that_appears_mid_job_fails_that_file_and_keeps_it()
    {
        using var h = new RunnerHarness();
        var plan = PlanCard(h, ("a.bin", TestDir.RandomBytes(100)), ("b.bin", TestDir.RandomBytes(100)));
        h.Dir.Write(@"backup\card\a.bin", [7]); // appears after planning
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var done = h.WaitFor(s => s.State == JobState.Done);

        Assert.Equal((2, 1, 1), (done.TotalFiles, done.DoneFiles, done.FailedFiles));
        Assert.Contains("appeared", done.LastError);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(h.Dir.PathOf(@"backup\card\a.bin")));
    }

    [Fact]
    public void Two_verification_failures_fail_the_file_and_the_job_still_finishes()
    {
        using var h = new RunnerHarness(new FaultyFileSystem(new WindowsFileSystem()) { CorruptVerifyReads = true });
        var plan = PlanCard(h, ("a.bin", TestDir.RandomBytes(100_000)));
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var done = h.WaitFor(s => s.State == JobState.Done);

        Assert.Equal(1, done.FailedFiles);
        Assert.Contains("twice", done.LastError);
    }

    [Fact]
    public void An_empty_folder_finishes_immediately()
    {
        using var h = new RunnerHarness();
        var plan = PlanCard(h);
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var done = h.WaitFor(s => s.State == JobState.Done);

        Assert.Equal((0, 1.0), (done.TotalFiles, done.Fraction));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~JobRunnerTests`
Expected: build FAILS — `JobRunner`, `JobSnapshot` and `OverwritePolicy` don't exist.

- [ ] **Step 4: Write the implementation**

`src/Squeue.Core/Jobs/JobQueue.cs`:

```csharp
using Squeue.Core.Copy;
using Squeue.Core.State;

namespace Squeue.Core.Jobs;

/// What to do with files that already exist at the destination.
public enum OverwritePolicy { Replace, Skip }

/// A job as the UI sees it. DoneBytes counts finished files (done or failed) plus the current file's copied bytes.
public sealed record JobSnapshot(
    long Id,
    string Name,
    string Source,
    string DestRoot,
    JobState State,
    int TotalFiles,
    int DoneFiles,
    int FailedFiles,
    long TotalBytes,
    long DoneBytes,
    string? CurrentFile,
    double BytesPerSecond,
    string? LastError,
    DateTime CreatedAtUtc)
{
    public double Fraction => TotalBytes == 0
        ? (State == JobState.Done ? 1 : 0)
        : Math.Min(1, (double)DoneBytes / TotalBytes);
}

public interface IJobQueue : IDisposable
{
    /// Raised on the queue's own thread, never the caller's.
    event Action<JobSnapshot>? JobChanged;

    void Start();
    void Enqueue(JobPlan plan, OverwritePolicy policy, bool verify);
    void Pause(long jobId);
    void Resume(long jobId);
    void Cancel(long jobId);
}

public sealed record JobRunnerOptions
{
    public CopyOptions Copy { get; init; } = new();

    /// Minimum time between progress snapshots of a running job. State changes are always published at once.
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}
```

`src/Squeue.Core/Jobs/JobRunner.cs`:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Core.Jobs;

/// Runs queued jobs one at a time on its own thread, which is the only thread that touches the journal.
/// Commands can be called from any thread; they take effect between files, and Pause/Cancel also stop the current file.
public sealed class JobRunner : IJobQueue
{
    private readonly IFileSystem _fs;
    private readonly string _journalPath;
    private readonly JobRunnerOptions _options;
    private readonly BlockingCollection<Action> _commands = new();
    private readonly object _gate = new();
    private long _runningJobId;
    private CancellationTokenSource? _runningStop;
    private Thread? _thread;
    private Journal? _journal;

    public JobRunner(IFileSystem fs, string journalPath, JobRunnerOptions? options = null)
    {
        _fs = fs;
        _journalPath = journalPath;
        _options = options ?? new JobRunnerOptions();
    }

    public event Action<JobSnapshot>? JobChanged;

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("The runner is already started.");
        _thread = new Thread(Run) { IsBackground = true, Name = "Squeue job runner" };
        _thread.Start();
    }

    public void Enqueue(JobPlan plan, OverwritePolicy policy, bool verify) =>
        _commands.Add(() => AddJob(plan, policy, verify));

    public void Pause(long jobId)
    {
        StopIfRunning(jobId);
        _commands.Add(() => ChangeState(jobId, JobState.Paused, JobState.Queued, JobState.Running));
    }

    public void Resume(long jobId) =>
        _commands.Add(() => ChangeState(jobId, JobState.Queued, JobState.Paused));

    public void Cancel(long jobId)
    {
        StopIfRunning(jobId);
        _commands.Add(() => ChangeState(jobId, JobState.Cancelled, JobState.Queued, JobState.Running, JobState.Paused));
    }

    /// Stops the current file (cleaned up, or left to finish next launch) and ends the runner thread.
    public void Dispose()
    {
        _commands.CompleteAdding();
        lock (_gate) _runningStop?.Cancel();
        _thread?.Join();
        _commands.Dispose();
    }

    private void Run()
    {
        using var journal = Journal.Open(_journalPath);
        _journal = journal;
        new Reconciler(_fs, journal).Run();
        foreach (var job in journal.Jobs())
        {
            // A job that was running when the app stopped goes back in the queue.
            if (job.State == JobState.Running) journal.SetJobState(job.Id, JobState.Queued);
        }
        foreach (var job in journal.Jobs()) Publish(SnapshotOf(job.Id));

        var copier = new FileCopier(_fs, journal, _options.Copy);
        while (!_commands.IsAddingCompleted)
        {
            DrainCommands();
            var next = journal.Jobs().FirstOrDefault(j => j.State == JobState.Queued);
            if (next is not null)
            {
                RunJob(copier, next.Id);
                continue;
            }
            try { _commands.Take()(); } // nothing queued: wait for a command
            catch (InvalidOperationException) { break; } // Dispose was called while waiting
        }
    }

    private void RunJob(FileCopier copier, long jobId)
    {
        var journal = _journal!;
        journal.SetJobState(jobId, JobState.Running);
        var job = journal.GetJob(jobId);
        var entries = journal.EntriesOf(jobId);
        var counters = JobCounters.From(entries);
        var stop = new CancellationTokenSource();
        lock (_gate)
        {
            _runningJobId = jobId;
            _runningStop = stop;
        }

        var clock = Stopwatch.StartNew();
        var sincePublish = Stopwatch.StartNew();
        long copiedThisRun = 0;
        long currentBytes = 0;
        string? currentFile = null;

        void PublishRunning(bool force)
        {
            if (!force && sincePublish.Elapsed < _options.ProgressInterval) return;
            sincePublish.Restart();
            double seconds = clock.Elapsed.TotalSeconds;
            double speed = seconds > 0.2 ? (copiedThisRun + currentBytes) / seconds : 0;
            Publish(Snapshot(job, counters, counters.FinishedBytes + currentBytes, currentFile, speed));
        }

        var progress = new InlineProgress(p =>
        {
            // Verification re-reads the file; only the copy stage moves the bar.
            if (p.Stage == CopyStage.Copying) currentBytes = p.BytesDone;
            PublishRunning(force: false);
        });

        try
        {
            PublishRunning(force: true);
            foreach (var entry in entries)
            {
                if (entry.State is EntryState.Done or EntryState.Failed) continue;
                DrainCommands();
                if (stop.IsCancellationRequested || journal.GetJob(jobId).State != JobState.Running) break;

                currentFile = Path.GetFileName(entry.SrcPath);
                currentBytes = 0;
                var result = CopyOne(copier, entry.Id, progress, stop.Token);
                if (result.Outcome == CopyOutcome.Cancelled) break;

                currentBytes = 0;
                if (result.Outcome == CopyOutcome.Done)
                {
                    counters.Done(entry.PlannedSize);
                    copiedThisRun += entry.PlannedSize;
                }
                else
                {
                    counters.Failed(entry.PlannedSize, result.Message);
                }
                PublishRunning(force: true);
            }
        }
        finally
        {
            lock (_gate)
            {
                _runningJobId = 0;
                _runningStop = null;
            }
            stop.Dispose();
        }

        DrainCommands();
        if (journal.GetJob(jobId).State == JobState.Running)
        {
            bool finished = journal.EntriesOf(jobId).All(e => e.State is EntryState.Done or EntryState.Failed);
            journal.SetJobState(jobId, finished ? JobState.Done : JobState.Queued);
        }
        Publish(SnapshotOf(jobId));
    }

    private CopyResult CopyOne(FileCopier copier, long entryId, IProgress<CopyProgress> progress, CancellationToken stop)
    {
        var journal = _journal!;
        try
        {
            var result = copier.CopyEntry(entryId, progress, stop);
            if (result.Outcome == CopyOutcome.RetryNeeded) result = copier.CopyEntry(entryId, progress, stop);

            if (result.Outcome == CopyOutcome.DestinationChanged)
            {
                // There is no per-file conflict prompt yet: keep the other file and report this one.
                const string message = "A file with this name appeared at the destination while copying. It was kept.";
                journal.SetEntryState(entryId, EntryState.Failed, message);
                return new CopyResult(CopyOutcome.Failed, message);
            }
            if (result.Outcome == CopyOutcome.RetryNeeded)
            {
                journal.SetEntryState(entryId, EntryState.Failed, result.Message);
                return new CopyResult(CopyOutcome.Failed, result.Message);
            }
            return result;
        }
        catch (IOException ex)
        {
            journal.SetEntryState(entryId, EntryState.Failed, ex.Message);
            return new CopyResult(CopyOutcome.Failed, ex.Message);
        }
    }

    private void AddJob(JobPlan plan, OverwritePolicy policy, bool verify)
    {
        var journal = _journal!;
        long jobId = 0;
        journal.Atomically(() =>
        {
            jobId = journal.CreateJob(verify ? "xxh3" : null, plan.Name, plan.Source, plan.DestRoot);
            foreach (var file in plan.Files)
            {
                if (file.ExistingDest is not null && policy == OverwritePolicy.Skip) continue;
                var action = file.ExistingDest is null ? ConflictAction.Create : ConflictAction.Replace;
                journal.AddEntry(jobId, file.SourcePath, file.DestPath, file.Size, action, file.ExistingDest);
            }
        });
        Publish(SnapshotOf(jobId));
    }

    private void ChangeState(long jobId, JobState to, params JobState[] from)
    {
        var journal = _journal!;
        if (!from.Contains(journal.GetJob(jobId).State)) return;
        journal.SetJobState(jobId, to);
        Publish(SnapshotOf(jobId));
    }

    private void StopIfRunning(long jobId)
    {
        lock (_gate)
        {
            if (_runningJobId == jobId) _runningStop?.Cancel();
        }
    }

    private void DrainCommands()
    {
        while (_commands.TryTake(out var command)) command();
    }

    private JobSnapshot SnapshotOf(long jobId)
    {
        var journal = _journal!;
        var counters = JobCounters.From(journal.EntriesOf(jobId));
        return Snapshot(journal.GetJob(jobId), counters, counters.FinishedBytes, null, 0);
    }

    private static JobSnapshot Snapshot(JobRow job, JobCounters c, long doneBytes, string? currentFile, double speed) =>
        new(job.Id, job.Name ?? "Copy", job.Source ?? "", job.DestRoot ?? "", job.State,
            c.Total, c.DoneCount, c.FailedCount, c.TotalBytes, doneBytes, currentFile, speed, c.LastError,
            DateTime.FromFileTimeUtc(job.CreatedAt));

    private void Publish(JobSnapshot snapshot) => JobChanged?.Invoke(snapshot);

    private sealed class InlineProgress(Action<CopyProgress> report) : IProgress<CopyProgress>
    {
        public void Report(CopyProgress value) => report(value);
    }

    private sealed class JobCounters
    {
        public int Total { get; private set; }
        public int DoneCount { get; private set; }
        public int FailedCount { get; private set; }
        public long TotalBytes { get; private set; }
        public long FinishedBytes { get; private set; }
        public string? LastError { get; private set; }

        public static JobCounters From(IReadOnlyList<EntryRow> entries)
        {
            var c = new JobCounters();
            foreach (var e in entries)
            {
                c.Total++;
                c.TotalBytes += e.PlannedSize;
                if (e.State == EntryState.Done) c.Done(e.PlannedSize);
                else if (e.State == EntryState.Failed) c.Failed(e.PlannedSize, e.Error);
            }
            return c;
        }

        public void Done(long bytes)
        {
            DoneCount++;
            FinishedBytes += bytes;
        }

        public void Failed(long bytes, string? error)
        {
            FailedCount++;
            FinishedBytes += bytes;
            LastError = error;
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — all tests, 8 new.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: job runner with pause, resume, cancel and restart recovery" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: View models

**Files:**
- Create: `src/Squeue.ViewModels/Squeue.ViewModels.csproj`, `Format.cs`, `Drives.cs`, `JobCardViewModel.cs`, `PlanViewModel.cs`, `MainViewModel.cs`
- Modify: `tests/Squeue.Tests/Squeue.Tests.csproj` (add project reference)
- Test: `tests/Squeue.Tests/ViewModels/FakeJobQueue.cs`, `ViewModelTests.cs`, `FormatTests.cs`

**Interfaces:**
- Consumes: `IJobQueue`, `JobSnapshot`, `JobPlan`, `OverwritePolicy`, `JobState` (Tasks 1, 3, 4).
- Produces (namespace `Squeue.ViewModels`):
  - `static class Format { Bytes(long), Speed(double), TimeLeft(TimeSpan), Count(int, string noun) }` — invariant culture.
  - `interface IDriveSource { IReadOnlyList<DriveInfoLite> ReadyDrives(); }`, `sealed record DriveInfoLite(string Root, string Label)`, `sealed partial class DriveViewModel` (`Root`, `Name`, `IsBusy`).
  - `sealed partial class JobCardViewModel` (`Id`, `Title`, `Route`, `Fraction`, `Percent`, `Status`, `TimeLeft`, `State`, `IsRunning`, `CanPause`, `CanResume`, `PauseCommand`, `ResumeCommand`, `CancelCommand`, `void Update(JobSnapshot)`).
  - `sealed partial class PlanViewModel` (`Plan`, `Title`, `Summary`, `ExistingNote`, `HasExisting`, `Overwrite`, `Verify`, `Policy`, `StartCommand`, `DismissCommand`).
  - `sealed partial class MainViewModel(IJobQueue queue, IDriveSource drives, Action<Action> post, Func<DateTime>? now = null)` (`Jobs`, `Drives`, `Subtitle`, `DoneToday`, `PendingPlan`, `HasPlan`, `Apply(JobSnapshot)`, `ProposePlan(JobPlan)`, `RefreshDrivesCommand`). `post` marshals runner-thread events to the UI thread.

- [ ] **Step 1: Create the project**

```bash
dotnet new classlib -n Squeue.ViewModels -o src/Squeue.ViewModels -f net10.0
dotnet sln Squeue.slnx add src/Squeue.ViewModels/Squeue.ViewModels.csproj
dotnet add src/Squeue.ViewModels/Squeue.ViewModels.csproj package CommunityToolkit.Mvvm
dotnet add src/Squeue.ViewModels/Squeue.ViewModels.csproj reference src/Squeue.Core/Squeue.Core.csproj
dotnet add tests/Squeue.Tests/Squeue.Tests.csproj reference src/Squeue.ViewModels/Squeue.ViewModels.csproj
```

Delete `src/Squeue.ViewModels/Class1.cs`. In `src/Squeue.ViewModels/Squeue.ViewModels.csproj` set `<TargetFramework>net10.0-windows</TargetFramework>` and make sure `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` are present.

- [ ] **Step 2: Write the failing tests**

`tests/Squeue.Tests/ViewModels/FakeJobQueue.cs`:

```csharp
using Squeue.Core.Jobs;
using Squeue.Core.State;
using Squeue.ViewModels;

namespace Squeue.Tests.ViewModels;

public sealed class FakeJobQueue : IJobQueue
{
    public event Action<JobSnapshot>? JobChanged;
    public List<string> Calls { get; } = [];

    public void Raise(JobSnapshot snapshot) => JobChanged?.Invoke(snapshot);
    public void Start() => Calls.Add("Start");
    public void Enqueue(JobPlan plan, OverwritePolicy policy, bool verify) => Calls.Add($"Enqueue {plan.Name} {policy} {verify}");
    public void Pause(long jobId) => Calls.Add($"Pause {jobId}");
    public void Resume(long jobId) => Calls.Add($"Resume {jobId}");
    public void Cancel(long jobId) => Calls.Add($"Cancel {jobId}");
    public void Dispose() { }

    public static JobSnapshot Snapshot(long id = 1, JobState state = JobState.Running, long total = 100 * 1048576,
        long done = 25 * 1048576, double speed = 10 * 1048576, string source = @"E:\DCIM", string dest = @"F:\Backup",
        int files = 2, int failed = 0, DateTime? created = null) =>
        new(id, "DCIM", source, dest, state, files, 0, failed, total, done, "IMG_0001.CR3", speed, null,
            created ?? DateTime.UtcNow);
}

public sealed class FakeDrives(params DriveInfoLite[] drives) : IDriveSource
{
    public IReadOnlyList<DriveInfoLite> ReadyDrives() => drives;
}
```

`tests/Squeue.Tests/ViewModels/FormatTests.cs`:

```csharp
using Squeue.ViewModels;

namespace Squeue.Tests.ViewModels;

public class FormatTests
{
    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "2 KB")]
    [InlineData(10 * 1048576L, "10 MB")]
    [InlineData(1610612736L, "1.5 GB")]
    public void Bytes(long value, string expected) => Assert.Equal(expected, Format.Bytes(value));

    [Theory]
    [InlineData(30, "under a minute left")]
    [InlineData(90, "2 min left")]
    [InlineData(3 * 3600 + 5 * 60, "3 h 5 min left")]
    public void TimeLeft(int seconds, string expected) => Assert.Equal(expected, Format.TimeLeft(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Speed_and_count() =>
        Assert.Equal(("10 MB/s", "1 file", "1,204 files"), (Format.Speed(10 * 1048576), Format.Count(1, "file"), Format.Count(1204, "file")));
}
```

`tests/Squeue.Tests/ViewModels/ViewModelTests.cs`:

```csharp
using Squeue.Core.Jobs;
using Squeue.Core.State;
using Squeue.ViewModels;

namespace Squeue.Tests.ViewModels;

public class ViewModelTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 15, 0, 0, DateTimeKind.Local);

    private static MainViewModel NewMain(FakeJobQueue queue, params DriveInfoLite[] drives) =>
        new(queue, new FakeDrives(drives), action => action(), () => Now);

    [Fact]
    public void A_running_job_shows_a_card_with_progress_speed_and_time_left()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);

        queue.Raise(FakeJobQueue.Snapshot());

        var card = Assert.Single(main.Jobs);
        Assert.Equal(("DCIM", @"E:\DCIM → F:\Backup", "25%"), (card.Title, card.Route, card.Percent));
        Assert.Equal(("Copying · 10 MB/s", "under a minute left"), (card.Status, card.TimeLeft));
        Assert.Equal((true, true, false), (card.IsRunning, card.CanPause, card.CanResume));
        Assert.Equal("75 MB left · under a minute left", main.Subtitle);
    }

    [Fact]
    public void A_queued_job_says_it_is_waiting()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);

        queue.Raise(FakeJobQueue.Snapshot(state: JobState.Queued, done: 0, speed: 0));

        Assert.Equal("Waiting · 2 files, 100 MB", main.Jobs[0].Status);
        Assert.Equal("100 MB left", main.Subtitle);
    }

    [Fact]
    public void A_finished_job_leaves_the_list_and_counts_as_done_today()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);
        queue.Raise(FakeJobQueue.Snapshot());

        queue.Raise(FakeJobQueue.Snapshot(state: JobState.Done, done: 100 * 1048576, created: Now.ToUniversalTime()));

        Assert.Empty(main.Jobs);
        Assert.Equal("1 done today", main.DoneToday);
        Assert.Equal("Nothing to copy. Drop files or folders here.", main.Subtitle);
    }

    [Fact]
    public void Card_buttons_go_to_the_queue()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);
        queue.Raise(FakeJobQueue.Snapshot(id: 7));

        main.Jobs[0].PauseCommand.Execute(null);
        main.Jobs[0].ResumeCommand.Execute(null);
        main.Jobs[0].CancelCommand.Execute(null);

        Assert.Equal(new[] { "Pause 7", "Resume 7", "Cancel 7" }, queue.Calls);
    }

    [Fact]
    public void Starting_a_plan_enqueues_it_and_closes_the_panel()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);
        var plan = new JobPlan("DCIM", @"E:\DCIM", @"F:\Backup", [new PlannedFile(@"E:\DCIM\a", @"F:\Backup\DCIM\a", 10, null)]);

        main.ProposePlan(plan);
        Assert.True(main.HasPlan);
        main.PendingPlan!.StartCommand.Execute(null);

        Assert.Equal(new[] { "Enqueue DCIM Skip True" }, queue.Calls);
        Assert.False(main.HasPlan);
    }

    [Fact]
    public void The_plan_summary_mentions_existing_files()
    {
        var existing = new Squeue.Core.FileSystem.FileIdentity(1, (UInt128)2, 3, 4, 5, 6, 0);
        var plan = new JobPlan("DCIM", @"E:\DCIM", @"F:\Backup",
        [
            new PlannedFile(@"E:\DCIM\a", @"F:\Backup\DCIM\a", 1048576, existing),
            new PlannedFile(@"E:\DCIM\b", @"F:\Backup\DCIM\b", 1048576, null),
        ]);

        var vm = new PlanViewModel(plan, _ => { }, _ => { });

        Assert.Equal(("Copy DCIM", @"2 files · 2 MB to F:\Backup"), (vm.Title, vm.Summary));
        Assert.Equal((true, "1 file already exists at the destination"), (vm.HasExisting, vm.ExistingNote));
        vm.Overwrite = true;
        Assert.Equal(OverwritePolicy.Replace, vm.Policy);
    }

    [Fact]
    public void Drives_used_by_a_running_job_are_busy()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue, new(@"C:\", "C:"), new(@"E:\", "EOS_DIGITAL (E:)"), new(@"F:\", "Backup (F:)"));

        queue.Raise(FakeJobQueue.Snapshot());

        Assert.Equal(new[] { false, true, true }, main.Drives.Select(d => d.IsBusy));
        Assert.Equal("EOS_DIGITAL (E:)", main.Drives[1].Name);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~Squeue.Tests.ViewModels`
Expected: build FAILS — `Squeue.ViewModels` types don't exist.

- [ ] **Step 4: Write the implementation**

`src/Squeue.ViewModels/Format.cs`:

```csharp
using System.Globalization;

namespace Squeue.ViewModels;

/// Short, human text for sizes, speeds and times.
public static class Format
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => string.Create(Culture, $"{bytes} B"),
        < 1024L * 1024 => string.Create(Culture, $"{bytes / 1024.0:0} KB"),
        < 1024L * 1024 * 1024 => string.Create(Culture, $"{bytes / 1048576.0:0.#} MB"),
        _ => string.Create(Culture, $"{bytes / 1073741824.0:0.#} GB"),
    };

    public static string Speed(double bytesPerSecond) => $"{Bytes((long)bytesPerSecond)}/s";

    public static string TimeLeft(TimeSpan left)
    {
        if (left.TotalSeconds < 60) return "under a minute left";
        if (left.TotalMinutes < 60) return string.Create(Culture, $"{Math.Ceiling(left.TotalMinutes):0} min left");
        return string.Create(Culture, $"{(int)left.TotalHours} h {left.Minutes} min left");
    }

    public static string Count(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString("N0", Culture)} {noun}s";
}
```

`src/Squeue.ViewModels/Drives.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace Squeue.ViewModels;

public sealed record DriveInfoLite(string Root, string Label);

public interface IDriveSource
{
    IReadOnlyList<DriveInfoLite> ReadyDrives();
}

public sealed partial class DriveViewModel(DriveInfoLite drive) : ObservableObject
{
    public string Root { get; } = drive.Root;
    public string Name { get; } = drive.Label;

    [ObservableProperty]
    private bool _isBusy;
}
```

`src/Squeue.ViewModels/JobCardViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Squeue.Core.Jobs;
using Squeue.Core.State;

namespace Squeue.ViewModels;

public sealed partial class JobCardViewModel(long id, IJobQueue queue) : ObservableObject
{
    public long Id { get; } = id;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _route = "";
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private string _percent = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _timeLeft = "";
    [ObservableProperty] private JobState _state;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canResume;

    public void Update(JobSnapshot s)
    {
        Title = s.Name;
        Route = $"{s.Source} → {s.DestRoot}";
        Fraction = s.Fraction;
        Percent = $"{Math.Round(s.Fraction * 100):0}%";
        State = s.State;
        IsRunning = s.State == JobState.Running;
        CanPause = s.State is JobState.Running or JobState.Queued;
        CanResume = s.State == JobState.Paused;
        Status = StatusText(s);
        TimeLeft = s.State == JobState.Running && s.BytesPerSecond > 0
            ? Format.TimeLeft(TimeSpan.FromSeconds((s.TotalBytes - s.DoneBytes) / s.BytesPerSecond))
            : "";
    }

    public static string StatusText(JobSnapshot s) => s.State switch
    {
        JobState.Running => s.BytesPerSecond > 0 ? $"Copying · {Format.Speed(s.BytesPerSecond)}" : "Copying",
        JobState.Queued => $"Waiting · {Format.Count(s.TotalFiles, "file")}, {Format.Bytes(s.TotalBytes)}",
        JobState.Paused => "Paused",
        JobState.Cancelled => "Cancelled",
        _ => s.FailedFiles > 0 ? $"Done · {s.FailedFiles} failed" : "Done",
    };

    [RelayCommand]
    private void Pause() => queue.Pause(Id);

    [RelayCommand]
    private void Resume() => queue.Resume(Id);

    [RelayCommand]
    private void Cancel() => queue.Cancel(Id);
}
```

`src/Squeue.ViewModels/PlanViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Squeue.Core.Jobs;

namespace Squeue.ViewModels;

/// The short confirmation shown before a job is queued.
public sealed partial class PlanViewModel(JobPlan plan, Action<PlanViewModel> start, Action<PlanViewModel> dismiss) : ObservableObject
{
    public JobPlan Plan { get; } = plan;
    public string Title => $"Copy {Plan.Name}";
    public string Summary => $"{Format.Count(Plan.Files.Count, "file")} · {Format.Bytes(Plan.TotalBytes)} to {Plan.DestRoot}";
    public bool HasExisting => Plan.ExistingCount > 0;

    public string? ExistingNote => Plan.ExistingCount switch
    {
        0 => null,
        1 => "1 file already exists at the destination",
        var n => $"{Format.Count(n, "file")} already exist at the destination",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Policy))]
    private bool _overwrite;

    [ObservableProperty]
    private bool _verify = true;

    public OverwritePolicy Policy => Overwrite ? OverwritePolicy.Replace : OverwritePolicy.Skip;

    [RelayCommand]
    private void Start() => start(this);

    [RelayCommand]
    private void Dismiss() => dismiss(this);
}
```

`src/Squeue.ViewModels/MainViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Squeue.Core.Jobs;
using Squeue.Core.State;

namespace Squeue.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IJobQueue _queue;
    private readonly IDriveSource _drives;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<long, JobCardViewModel> _cards = [];
    private readonly Dictionary<long, JobSnapshot> _latest = [];

    /// <param name="post">Runs an action on the UI thread; the queue raises events on its own thread.</param>
    public MainViewModel(IJobQueue queue, IDriveSource drives, Action<Action> post, Func<DateTime>? now = null)
    {
        _queue = queue;
        _drives = drives;
        _now = now ?? (() => DateTime.Now);
        queue.JobChanged += snapshot => post(() => Apply(snapshot));
        RefreshDrives();
        UpdateSummary();
    }

    public ObservableCollection<JobCardViewModel> Jobs { get; } = [];
    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _doneToday = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    private PlanViewModel? _pendingPlan;

    public bool HasPlan => PendingPlan is not null;

    public void Apply(JobSnapshot snapshot)
    {
        _latest[snapshot.Id] = snapshot;
        if (IsActive(snapshot))
        {
            if (!_cards.TryGetValue(snapshot.Id, out var card))
            {
                card = new JobCardViewModel(snapshot.Id, _queue);
                _cards[snapshot.Id] = card;
                Jobs.Add(card);
            }
            card.Update(snapshot);
        }
        else if (_cards.Remove(snapshot.Id, out var finished))
        {
            Jobs.Remove(finished);
        }
        UpdateSummary();
        UpdateBusyDrives();
    }

    public void ProposePlan(JobPlan plan) => PendingPlan = new PlanViewModel(plan, StartPlan, _ => PendingPlan = null);

    [RelayCommand]
    public void RefreshDrives()
    {
        Drives.Clear();
        foreach (var drive in _drives.ReadyDrives()) Drives.Add(new DriveViewModel(drive));
        UpdateBusyDrives();
    }

    private void StartPlan(PlanViewModel plan)
    {
        _queue.Enqueue(plan.Plan, plan.Policy, plan.Verify);
        PendingPlan = null;
    }

    private void UpdateSummary()
    {
        var active = _latest.Values.Where(IsActive).ToList();
        long left = active.Sum(s => s.TotalBytes - s.DoneBytes);
        double speed = active.Where(s => s.State == JobState.Running).Sum(s => s.BytesPerSecond);
        Subtitle = active.Count == 0
            ? "Nothing to copy. Drop files or folders here."
            : speed > 0
                ? $"{Format.Bytes(left)} left · {Format.TimeLeft(TimeSpan.FromSeconds(left / speed))}"
                : $"{Format.Bytes(left)} left";

        int doneToday = _latest.Values.Count(s => s.State == JobState.Done && s.CreatedAtUtc.ToLocalTime().Date == _now().Date);
        DoneToday = doneToday == 0 ? "" : $"{doneToday} done today";
    }

    private void UpdateBusyDrives()
    {
        var busyRoots = _latest.Values
            .Where(s => s.State == JobState.Running)
            .SelectMany(s => new[] { Path.GetPathRoot(s.Source), Path.GetPathRoot(s.DestRoot) })
            .Where(root => !string.IsNullOrEmpty(root))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in Drives) drive.IsBusy = busyRoots.Contains(drive.Root);
    }

    private static bool IsActive(JobSnapshot s) => s.State is JobState.Queued or JobState.Running or JobState.Paused;
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — all tests, 16 new, and no warnings from the build. If CommunityToolkit.Mvvm reports warning MVVMTK0045 (it prefers partial properties) keep the field style and add `<NoWarn>$(NoWarn);MVVMTK0045</NoWarn>` to `Squeue.ViewModels.csproj`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: view models for the queue window" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: The WPF window

**Files:**
- Create: `src/Squeue.App/Squeue.App.csproj`, `App.xaml`, `App.xaml.cs`, `SystemDrives.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `JobRunner`, `JobPlanner`, `WindowsFileSystem` (Core), `MainViewModel`, `IDriveSource`, `DriveInfoLite` (ViewModels).
- Produces: `Squeue.exe`, the runnable app.

- [ ] **Step 1: Create the project**

```bash
dotnet new wpf -n Squeue.App -o src/Squeue.App -f net10.0
dotnet sln Squeue.slnx add src/Squeue.App/Squeue.App.csproj
```

Delete the generated `MainWindow.xaml`, `MainWindow.xaml.cs`, `App.xaml`, `App.xaml.cs` and `AssemblyInfo.cs` if present (they are replaced below).

Replace `src/Squeue.App/Squeue.App.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <AssemblyName>Squeue</AssemblyName>
    <RootNamespace>Squeue.App</RootNamespace>
    <!-- ThemeMode (the Fluent theme) is flagged as experimental in some SDKs. -->
    <NoWarn>$(NoWarn);WPF0001</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Squeue.Core\Squeue.Core.csproj" />
    <ProjectReference Include="..\Squeue.ViewModels\Squeue.ViewModels.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the app**

`src/Squeue.App/App.xaml`:

```xml
<Application x:Class="Squeue.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ThemeMode="System"
             ShutdownMode="OnMainWindowClose">
    <Application.Resources>
        <BooleanToVisibilityConverter x:Key="Visible" />
    </Application.Resources>
</Application>
```

`src/Squeue.App/App.xaml.cs`:

```csharp
using System.Windows;
using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;
using Squeue.ViewModels;

namespace Squeue.App;

public partial class App : Application
{
    private JobRunner? _runner;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string journalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Squeue", "state.db");
        _runner = new JobRunner(new WindowsFileSystem(), journalPath);
        var viewModel = new MainViewModel(_runner, new SystemDrives(), action => Dispatcher.BeginInvoke(action));
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
        _runner.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stops the current file safely; unfinished jobs carry on at the next launch.
        _runner?.Dispose();
        base.OnExit(e);
    }
}
```

`src/Squeue.App/SystemDrives.cs`:

```csharp
using Squeue.ViewModels;

namespace Squeue.App;

internal sealed class SystemDrives : IDriveSource
{
    public IReadOnlyList<DriveInfoLite> ReadyDrives() =>
        DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d =>
            {
                string letter = d.Name.TrimEnd('\\');
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? letter : $"{d.VolumeLabel} ({letter})";
                return new DriveInfoLite(d.RootDirectory.FullName, label);
            })
            .ToList();
}
```

`src/Squeue.App/MainWindow.xaml`:

```xml
<Window x:Class="Squeue.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:Squeue.ViewModels;assembly=Squeue.ViewModels"
        Title="Squeue" Width="460" Height="660" MinWidth="360" MinHeight="420"
        WindowStartupLocation="CenterScreen"
        AllowDrop="True" DragOver="OnDragOver" Drop="OnDrop">
    <Window.Resources>
        <Style x:Key="IconButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
            <Setter Property="Width" Value="32" />
            <Setter Property="Height" Value="32" />
            <Setter Property="Padding" Value="0" />
            <Setter Property="FontFamily" Value="Segoe Fluent Icons, Segoe MDL2 Assets" />
            <Setter Property="FontSize" Value="14" />
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="BorderThickness" Value="0" />
        </Style>

        <!-- New cards fade and rise into place; nothing bounces. -->
        <Storyboard x:Key="Appear">
            <DoubleAnimation Storyboard.TargetProperty="Opacity" From="0" To="1" Duration="0:0:0.2" />
            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.Y)"
                             From="8" To="0" Duration="0:0:0.25">
                <DoubleAnimation.EasingFunction>
                    <CubicEase EasingMode="EaseOut" />
                </DoubleAnimation.EasingFunction>
            </DoubleAnimation>
        </Storyboard>

        <DataTemplate x:Key="JobCard" DataType="{x:Type vm:JobCardViewModel}">
            <Border x:Name="Card" CornerRadius="12" Padding="16,14" Margin="0,0,0,8"
                    Background="{DynamicResource CardBackgroundFillColorDefaultBrush}"
                    BorderBrush="{DynamicResource CardStrokeColorDefaultBrush}" BorderThickness="1">
                <Border.RenderTransform>
                    <TranslateTransform />
                </Border.RenderTransform>
                <Border.Triggers>
                    <EventTrigger RoutedEvent="Loaded">
                        <BeginStoryboard Storyboard="{StaticResource Appear}" />
                    </EventTrigger>
                </Border.Triggers>
                <StackPanel>
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <StackPanel>
                            <TextBlock Text="{Binding Title}" FontSize="14" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                            <TextBlock Text="{Binding Route}" FontSize="12" Margin="0,2,0,0" TextTrimming="CharacterEllipsis"
                                       Foreground="{DynamicResource TextFillColorSecondaryBrush}" />
                        </StackPanel>
                        <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Top">
                            <TextBlock Text="{Binding Percent}" FontSize="13" FontWeight="SemiBold" VerticalAlignment="Center"
                                       Margin="0,0,6,0" Visibility="{Binding IsRunning, Converter={StaticResource Visible}}" />
                            <Button Style="{StaticResource IconButton}" Content="&#xE769;" ToolTip="Pause"
                                    Command="{Binding PauseCommand}" Visibility="{Binding CanPause, Converter={StaticResource Visible}}" />
                            <Button Style="{StaticResource IconButton}" Content="&#xE768;" ToolTip="Resume"
                                    Command="{Binding ResumeCommand}" Visibility="{Binding CanResume, Converter={StaticResource Visible}}" />
                            <Button Style="{StaticResource IconButton}" Content="&#xE711;" ToolTip="Cancel"
                                    Command="{Binding CancelCommand}" />
                        </StackPanel>
                    </Grid>
                    <ProgressBar Maximum="1" Value="{Binding Fraction, Mode=OneWay}" Height="3" Margin="0,12,0,0" />
                    <Grid Margin="0,6,0,0">
                        <TextBlock Text="{Binding Status}" FontSize="11" Foreground="{DynamicResource TextFillColorTertiaryBrush}" />
                        <TextBlock Text="{Binding TimeLeft}" FontSize="11" HorizontalAlignment="Right"
                                   Foreground="{DynamicResource TextFillColorTertiaryBrush}" />
                    </Grid>
                </StackPanel>
            </Border>
            <DataTemplate.Triggers>
                <!-- Only the running job is a solid card; waiting and paused jobs are outlined. -->
                <DataTrigger Binding="{Binding IsRunning}" Value="False">
                    <Setter TargetName="Card" Property="Background" Value="Transparent" />
                </DataTrigger>
            </DataTemplate.Triggers>
        </DataTemplate>
    </Window.Resources>

    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <Grid>
            <StackPanel>
                <TextBlock Text="Queue" FontSize="26" FontWeight="SemiBold" />
                <TextBlock Text="{Binding Subtitle}" FontSize="12" Margin="0,2,0,0" TextWrapping="Wrap"
                           Foreground="{DynamicResource TextFillColorSecondaryBrush}" />
            </StackPanel>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Top">
                <Button Style="{StaticResource IconButton}" Content="&#xE8B7;" ToolTip="Copy folders" Click="OnAddFolders" />
                <Button Style="{StaticResource IconButton}" Content="&#xE8E5;" ToolTip="Copy files" Click="OnAddFiles" />
            </StackPanel>
        </Grid>

        <ItemsControl Grid.Row="1" ItemsSource="{Binding Drives}" Margin="0,16,0,12">
            <ItemsControl.ItemsPanel>
                <ItemsPanelTemplate>
                    <WrapPanel />
                </ItemsPanelTemplate>
            </ItemsControl.ItemsPanel>
            <ItemsControl.ItemTemplate>
                <DataTemplate DataType="{x:Type vm:DriveViewModel}">
                    <Border CornerRadius="10" Padding="10,4" Margin="0,0,6,6"
                            Background="{DynamicResource SubtleFillColorSecondaryBrush}">
                        <StackPanel Orientation="Horizontal">
                            <Ellipse x:Name="Dot" Width="6" Height="6" Margin="0,0,6,0" VerticalAlignment="Center"
                                     Fill="{DynamicResource ControlStrongStrokeColorDisabledBrush}" />
                            <TextBlock Text="{Binding Name}" FontSize="12" Foreground="{DynamicResource TextFillColorSecondaryBrush}" />
                        </StackPanel>
                    </Border>
                    <DataTemplate.Triggers>
                        <DataTrigger Binding="{Binding IsBusy}" Value="True">
                            <Setter TargetName="Dot" Property="Fill" Value="{DynamicResource TextFillColorPrimaryBrush}" />
                        </DataTrigger>
                    </DataTemplate.Triggers>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>

        <Border Grid.Row="2" CornerRadius="12" Padding="16" Margin="0,0,0,12" BorderThickness="1"
                BorderBrush="{DynamicResource AccentFillColorDefaultBrush}"
                Visibility="{Binding HasPlan, Converter={StaticResource Visible}}">
            <StackPanel DataContext="{Binding PendingPlan}">
                <TextBlock Text="{Binding Title}" FontSize="14" FontWeight="SemiBold" />
                <TextBlock Text="{Binding Summary}" FontSize="12" Margin="0,2,0,10" TextWrapping="Wrap"
                           Foreground="{DynamicResource TextFillColorSecondaryBrush}" />
                <TextBlock Text="{Binding ExistingNote}" FontSize="12"
                           Visibility="{Binding HasExisting, Converter={StaticResource Visible}}" />
                <CheckBox Content="Replace them" IsChecked="{Binding Overwrite}" Margin="0,4,0,0"
                          Visibility="{Binding HasExisting, Converter={StaticResource Visible}}" />
                <CheckBox Content="Verify every file after copying" IsChecked="{Binding Verify}" Margin="0,4,0,0" />
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
                    <Button Content="Cancel" Command="{Binding DismissCommand}" Margin="0,0,8,0" />
                    <Button Content="Start copying" Command="{Binding StartCommand}" Style="{DynamicResource AccentButtonStyle}" />
                </StackPanel>
            </StackPanel>
        </Border>

        <ScrollViewer Grid.Row="3" VerticalScrollBarVisibility="Auto">
            <ItemsControl ItemsSource="{Binding Jobs}" ItemTemplate="{StaticResource JobCard}" />
        </ScrollViewer>

        <TextBlock Grid.Row="4" Text="{Binding DoneToday}" FontSize="12" Margin="4,8,0,0"
                   Foreground="{DynamicResource TextFillColorTertiaryBrush}" />
    </Grid>
</Window>
```

`src/Squeue.App/MainWindow.xaml.cs`:

```csharp
using System.Windows;
using Microsoft.Win32;
using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;
using Squeue.ViewModels;

namespace Squeue.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IFileSystem _fs = new WindowsFileSystem();

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        // Let Explorer finish the drop before showing a dialog.
        Dispatcher.BeginInvoke(() => ChooseDestinationAndPlan(paths));
    }

    private void OnAddFolders(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose folders to copy", Multiselect = true };
        if (dialog.ShowDialog(this) == true) ChooseDestinationAndPlan(dialog.FolderNames);
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose files to copy", Multiselect = true };
        if (dialog.ShowDialog(this) == true) ChooseDestinationAndPlan(dialog.FileNames);
    }

    private async void ChooseDestinationAndPlan(IReadOnlyList<string> sources)
    {
        var dialog = new OpenFolderDialog { Title = "Copy to" };
        if (dialog.ShowDialog(this) != true) return;
        string destination = dialog.FolderName;
        try
        {
            // Listing a big card can take a moment; keep the window responsive.
            var plan = await Task.Run(() => JobPlanner.Plan(_fs, sources, destination));
            _viewModel.ProposePlan(plan);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Squeue", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
```

- [ ] **Step 3: Build and run the tests**

Run: `dotnet build Squeue.slnx` then `dotnet test`
Expected: build succeeds with 0 warnings; all tests pass (the app has no unit tests; its logic lives in the view models).

- [ ] **Step 4: Launch and check by hand**

Run: `dotnet run --project src/Squeue.App`

Check, in order:
1. The window opens in the system light/dark theme, titled "Queue", with "Nothing to copy. Drop files or folders here." and one pill per ready drive.
2. Drag a folder of photos from Explorer onto the window, choose a destination: the plan panel shows "Copy <folder>", the file count and size. "Start copying" adds a card that fades in; its bar, percentage, speed and time left move; the drive pills of the source and destination show a filled dot.
3. Pause: the card turns outlined and says "Paused"; the destination has no `~tq….tmp` file. Resume: it finishes and the card leaves the list; "1 done today" appears.
4. Queue a second big folder, close the window mid-copy, relaunch: the job reappears and finishes.
5. Copy the same folder to the same destination again: the plan panel says the files already exist; with "Replace them" unticked the job finishes quickly without copying them.

Record anything that doesn't match in the task report.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: queue window" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## What this plan does not cover

| Item | Plan |
| --- | --- |
| Several jobs at once on different disks, capacity checks | 5 — Coordinator |
| Per-file conflict prompts, rename / keep both, overwrite older | 6 — Planning |
| Moves | 2 — Moves |
| History view, reports, compact progress window, end actions | 7 — WPF app (full) |
| Explorer right-click, CLI, installer | 8 — Integration |
| Speed tuning | 3 — Throughput |
