# Squeue Several Drives at Once Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Jobs on different physical disks run at the same time; jobs that share a disk run one after another in queue order; a card is released for the next job as soon as the previous job moves on to verifying; a job only starts when its destination has room.

**Architecture:**
- `IDiskTopology` maps a path to the physical disks under its volume (Windows: volume GUID → `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`; `net:<server>` for network shares; `unk:<volume>` when the disks can't be read). `IVolumeSpace` reads free space and cluster size.
- `Scheduler.Pass` is a pure function: queued jobs (in order, each with the disks it needs now) + disks held by running jobs → which jobs start, which wait (and on whom), which lack space. Table-tested.
- `Capacity` computes a job's need (files rounded up to clusters) against free space minus a headroom of max(1 GB, 1 % of the volume), minus what running jobs on that volume still have to write.
- `JobRunner` becomes a coordinator: one coordinator thread owns the queue, the held disks and every scheduling decision, with its own journal connection. Each started job runs in a `JobWorker` on its own thread with its own journal connection. The worker copies every file first (verification deferred), then tells the coordinator it is verifying — the coordinator releases the source disks and runs a pass — then verifies. A worker that stops (done, paused, cancelled, error) reports back; only then are its disks released.
- `FileCopier` gains `deferVerification`: publish now, verify in a later call (the journal already models this — an attempt left at `Published`).
- The journal allows several connections at once: `busy_timeout`, and `BEGIN IMMEDIATE` for multi-statement transactions.

**Tech Stack:** C# / .NET 10, xUnit, Win32 volume APIs, CommunityToolkit.Mvvm (view models).

**Spec:** Squeue — System Design Spec, Revision 3: https://claude.ai/code/artifact/e58e5773-986c-4724-8466-07c85a154be9 — "Endpoints, scheduler and resources" (Endpoint and disk resolution; Slots and phases, including "Card readers during verification"; Scheduling pass and fairness; Capacity ledger), and "Data model" (one writer / durable commit acknowledgement).

**Branch:** `feat/multi-drive` from `main` (created; the plan is its first commit).

## Scope and rulings made in this plan

- **Several journal connections instead of one writer thread.** The spec asks for one writer task with durable acknowledgements. Each thread having its own SQLite connection with `synchronous = FULL`, `busy_timeout` and `BEGIN IMMEDIATE` gives the same guarantee (a call returns only after its commit is on disk) with far less code. Revisit if lock waits show up in profiles.
- **Destination claims are left out.** With one slot per disk (v1), two jobs whose destination or source folders overlap are on the same volume, therefore the same disk, therefore already serialized by the disk rule. Claims return with multi-slot disks.
- **Out of scope:** reorder and "run now" (plan 7), "safe to remove card" (plan 7), a global buffer pool (workers are bounded by the number of disks; each holds one 16 MiB ring), per-job worker parallelism on SSDs (later), drive-arrival events (a paused job is resumed by the user).

## Global Constraints

- Plan-1 invariants hold: unflushed data never gets the final name; an existing destination is only replaced by a verified file; each journal phase commits before the filesystem change that depends on it; cleanup only by matching file id; uncertainty keeps data.
- A journal connection is used by one thread only. The coordinator's connection is only used on the coordinator thread; each worker opens its own. Never call the filesystem inside `Journal.Atomically`.
- Recovery (the Reconciler) run by a worker touches only its own job's entries — other workers may be mid-copy.
- A job's disks are released only after its worker has stopped (reported back); the coordinator ignores reports from a stale run.
- Strict start order per disk: a job never starts ahead of an earlier queued job that shares any of its disks.
- `JobChanged` may be raised on the coordinator or on a worker thread; subscribers marshal (the app already uses `Dispatcher.BeginInvoke`).
- UI copy: sentence case, no "please"/"successfully"/exclamation marks.
- Every commit message ends with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (two `-m` flags). Stage explicit paths; never commit `.superpowers/` or `tools/`.

## Review Focus

1. **A card job and a video job from the same card** → the second waits ("… is using the same drive"), then starts as soon as the first begins verifying. Task 5: `The_card_is_free_while_the_previous_job_verifies`.
2. **Two cards to two different drives** → both copy at once. Task 5: `Jobs_on_different_drives_run_at_the_same_time`.
3. **Pause or close while two jobs run** → each worker stops within a chunk, temp files are removed, disks are released only after it stopped, and the jobs resume on the next launch. Task 5 keeps every existing runner test; `Pausing_one_of_two_running_jobs_leaves_the_other_running`.
4. **A backup drive that's too full** → the job pauses with the amounts ("Not enough space on F:\ …"), a later job on other drives still starts. Task 4 table test + Task 5 `A_destination_without_enough_space_pauses_the_job`.
5. **Two connections writing the journal at the same moment** → no "database is locked" errors. Task 1: `Two_connections_can_write_at_the_same_time`.

---

## File structure

```
src/Squeue.Core/State/Journal.cs          busy_timeout; BEGIN IMMEDIATE
src/Squeue.Core/State/Records.cs          + JobState.Verifying
src/Squeue.Core/Copy/Reconciler.cs        + Run(Func<Entry, bool> include)
src/Squeue.Core/Copy/CopyTypes.cs         + CopyOutcome.Published
src/Squeue.Core/Copy/FileCopier.cs        + deferVerification
src/Squeue.Core/FileSystem/Volumes.cs     IDiskTopology, IVolumeSpace, VolumeSpace, WindowsDiskTopology, WindowsVolumeSpace
src/Squeue.Core/Jobs/Capacity.cs          need, headroom, problem text
src/Squeue.Core/Jobs/Scheduler.cs         SchedulerJob, Wait, SchedulerResult, Scheduler.Pass
src/Squeue.Core/Jobs/JobProgress.cs       JobCounters, Snapshots, DriveProblems (moved out of JobRunner)
src/Squeue.Core/Jobs/JobWorker.cs         one job's copy then verify phases
src/Squeue.Core/Jobs/JobRunner.cs         the coordinator (rewritten)
src/Squeue.Core/Jobs/JobQueue.cs          + JobSnapshot.WaitingFor
src/Squeue.ViewModels/JobCardViewModel.cs Verifying and waiting text
src/Squeue.ViewModels/MainViewModel.cs    Verifying is active; busy drives while verifying
tests/Squeue.Tests/State/JournalConcurrencyTests.cs
tests/Squeue.Tests/Copy/DeferredVerificationTests.cs
tests/Squeue.Tests/FileSystem/VolumeTests.cs
tests/Squeue.Tests/Jobs/CapacityTests.cs
tests/Squeue.Tests/Jobs/SchedulerTests.cs
tests/Squeue.Tests/Fakes/FolderTopology.cs, FakeVolumeSpace.cs, GateFileSystem.cs
tests/Squeue.Tests/Jobs/RunnerHarness.cs  + topology/space parameters
tests/Squeue.Tests/Jobs/MultiDriveTests.cs
tests/Squeue.Tests/ViewModels/ViewModelTests.cs  + verifying/waiting tests
```

---

### Task 1: Journal for several connections, Verifying state, scoped recovery

**Files:**
- Modify: `src/Squeue.Core/State/Journal.cs`, `src/Squeue.Core/State/Records.cs`, `src/Squeue.Core/Copy/Reconciler.cs`
- Test: `tests/Squeue.Tests/State/JournalConcurrencyTests.cs`, `tests/Squeue.Tests/Copy/ReconcilerTests.cs`

**Interfaces:**
- Produces: `JobState.Verifying` (appended at the end of the enum, so stored names and existing values don't move); `Reconciler.Run(Func<Entry, bool>? include)` — reconciles only attempts whose entry passes `include` (`Run()` = all).

- [ ] **Step 1: Write the failing tests**

`tests/Squeue.Tests/State/JournalConcurrencyTests.cs`:

```csharp
using Squeue.Core.State;

namespace Squeue.Tests.State;

public class JournalConcurrencyTests
{
    [Fact]
    public void Two_connections_can_write_at_the_same_time()
    {
        using var dir = new TestDir();
        string path = dir.PathOf("state.db");
        long job;
        using (var setup = Journal.Open(path)) job = setup.CreateJob(null);

        void Writer(string prefix)
        {
            using var journal = Journal.Open(path);
            for (int i = 0; i < 200; i++)
            {
                long entry = journal.AddEntry(job, $"{prefix}{i}", $"{prefix}{i}", 1, ConflictAction.Create, null);
                journal.Atomically(() =>
                {
                    long attempt = journal.BeginAttempt(entry, "~tqaaaaaaaaaa.tmp", null);
                    journal.SetEntryState(entry, EntryState.Active);
                    journal.SetPhase(attempt, AttemptPhase.Abandoned);
                });
            }
        }

        var a = Task.Run(() => Writer("a"));
        var b = Task.Run(() => Writer("b"));
        Task.WaitAll(a, b);

        using var check = Journal.Open(path);
        Assert.Equal(400, check.EntriesOf(job).Count);
        Assert.All(check.EntriesOf(job), e => Assert.Equal(EntryState.Active, e.State));
    }

    [Fact]
    public void Verifying_is_a_job_state()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long job = journal.CreateJob(null);
        journal.SetJobState(job, JobState.Verifying);
        Assert.Equal(JobState.Verifying, journal.GetJob(job).State);
    }
}
```

Add to `ReconcilerTests`:

```csharp
    [Fact]
    public void Run_can_be_limited_to_some_entries()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(1000));
        long mine = s.AddEntry();
        long other = s.AddEntry();
        s.Journal.BeginAttempt(mine, "~tqaaaaaaaaaa.tmp", null);
        s.Journal.BeginAttempt(other, "~tqbbbbbbbbbb.tmp", null);

        var report = new Reconciler(new WindowsFileSystem(), s.Journal).Run(entry => entry.Id == mine);

        Assert.Equal(new[] { mine }, report.Reset);
        Assert.NotNull(s.Journal.GetOpenAttempt(other));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~JournalConcurrencyTests|FullyQualifiedName~Run_can_be_limited"`
Expected: build FAILS (`JobState.Verifying`, `Run(Func<...>)` missing). After adding only the enum member and the overload, `Two_connections_can_write_at_the_same_time` fails with `SQLite Error 5: 'database is locked'` — confirm and record it.

- [ ] **Step 3: Implement**

`Records.cs`: change the enum to `public enum JobState { Queued, Running, Paused, Done, Cancelled, Verifying }` (appended, so stored names and existing values don't move).

`Journal.cs`:
- In `Open`, after `PRAGMA foreign_keys = ON;` add `journal.Exec("PRAGMA busy_timeout = 10000;");` with a comment: `// Several connections (the coordinator and each job's worker) share the file; wait for a writer instead of failing.`
- In `Atomically`, replace `_db.BeginTransaction()` with `_db.BeginTransaction(deferred: false)` and add the comment `// BEGIN IMMEDIATE: take the write lock up front, so a read-then-write block can't fail on another connection's commit.`

`Reconciler.cs`: replace `public ReconcileReport Run()` and its loop header with:

```csharp
    public ReconcileReport Run() => Run(null);

    /// Reconciles only the attempts whose entry passes <paramref name="include"/> — a job's worker must not touch
    /// attempts that another job's worker may still be writing.
    public ReconcileReport Run(Func<Entry, bool>? include)
    {
        var report = new ReconcileReport();
        foreach (var attempt in journal.OpenAttempts())
        {
            var entry = journal.GetEntry(attempt.EntryId);
            if (include is not null && !include(entry)) continue;
```

(keep the rest of the loop body exactly as it is).

- [ ] **Step 4: Run the tests**

Run: `dotnet test` (run `JournalConcurrencyTests` 3 times).
Expected: PASS — all tests, 3 new.

- [ ] **Step 5: Commit**

```bash
git add src/Squeue.Core/State/Journal.cs src/Squeue.Core/State/Records.cs src/Squeue.Core/Copy/Reconciler.cs tests/Squeue.Tests/State/JournalConcurrencyTests.cs tests/Squeue.Tests/Copy/ReconcilerTests.cs
git commit -m "feat: journal shared by several connections, verifying state, per-job recovery" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Deferred verification in the copier

**Files:**
- Modify: `src/Squeue.Core/Copy/CopyTypes.cs`, `src/Squeue.Core/Copy/FileCopier.cs`
- Test: `tests/Squeue.Tests/Copy/DeferredVerificationTests.cs`

**Interfaces:**
- Produces: `CopyOutcome.Published` ("copied and published; verification still to do"); `FileCopier.CopyEntry(long entryId, IProgress<CopyProgress>? progress = null, CancellationToken cancellationToken = default, bool deferVerification = false)`. With `deferVerification`, a copy that still needs a post-publish verification returns `Published` and leaves the entry `Active` with its attempt at `Published`; a later `CopyEntry` without the flag verifies and finishes it. Copies that need no separate verification (no hash, or a replacement already verified before publishing) finish as `Done`.

- [ ] **Step 1: Write the failing tests**

`tests/Squeue.Tests/Copy/DeferredVerificationTests.cs`:

```csharp
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Copy;

public class DeferredVerificationTests
{
    private const int Chunk = 64 * 1024;
    private static readonly CopyOptions Options = new() { ChunkSize = Chunk, SharingRetryTimeout = TimeSpan.Zero };
    private readonly WindowsFileSystem _fs = new();

    [Fact]
    public void A_deferred_copy_is_published_now_and_verified_later()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(3 * Chunk + 1);
        s.WriteSource(content);
        long id = s.AddEntry();
        var copier = new FileCopier(_fs, s.Journal, Options);

        var first = copier.CopyEntry(id, deferVerification: true);

        Assert.Equal(CopyOutcome.Published, first.Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
        Assert.Equal(EntryState.Active, s.Journal.GetEntry(id).State);
        Assert.Equal(AttemptPhase.Published, s.Journal.GetOpenAttempt(id)!.Phase);
        Assert.Null(s.Journal.GetEntry(id).DestHash);

        var second = copier.CopyEntry(id);

        Assert.Equal(CopyOutcome.Done, second.Outcome);
        Assert.Equal(s.Journal.GetEntry(id).SrcHash, s.Journal.GetEntry(id).DestHash);
        Assert.Single(s.Journal.AttemptsFor(id));
    }

    [Fact]
    public void Resuming_a_published_copy_with_deferral_leaves_it_published()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(1000));
        long id = s.AddEntry();
        var copier = new FileCopier(_fs, s.Journal, Options);
        copier.CopyEntry(id, deferVerification: true);

        Assert.Equal(CopyOutcome.Published, copier.CopyEntry(id, deferVerification: true).Outcome);
        Assert.Equal(AttemptPhase.Published, s.Journal.GetOpenAttempt(id)!.Phase);
    }

    [Fact]
    public void Without_a_hash_a_deferred_copy_finishes_at_once()
    {
        using var s = new CopyScenario();
        s.WriteSource([1, 2, 3]);
        long id = s.AddEntry(hash: null);

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id, deferVerification: true).Outcome);
    }

    [Fact]
    public void A_replacement_is_verified_before_publishing_even_when_deferred()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(1000));
        s.WriteDest([9]);
        long id = s.AddEntry(action: ConflictAction.Replace, seenDest: _fs.TryGetIdentity(s.Dest));

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id, deferVerification: true).Outcome);
    }

    [Fact]
    public void A_failed_deferred_verification_asks_for_a_new_copy()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(2 * Chunk));
        long id = s.AddEntry();
        var faulty = new FaultyFileSystem(_fs) { CorruptVerifyReads = true };
        var copier = new FileCopier(faulty, s.Journal, Options);
        Assert.Equal(CopyOutcome.Published, copier.CopyEntry(id, deferVerification: true).Outcome);

        var result = copier.CopyEntry(id);

        Assert.Equal(CopyOutcome.RetryNeeded, result.Outcome);
        Assert.Equal(EntryState.Pending, s.Journal.GetEntry(id).State);
        Assert.Equal(ConflictAction.Replace, s.Journal.GetEntry(id).Action);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~DeferredVerificationTests`
Expected: build FAILS — no `deferVerification` parameter, no `CopyOutcome.Published`.

- [ ] **Step 3: Implement**

`CopyTypes.cs`: add to `CopyOutcome`, after `Cancelled`:

```csharp
    /// Copied and published at its final name; its verification is still to do (see deferVerification).
    Published,
```

`FileCopier.cs`:
1. Change the signature to `public CopyResult CopyEntry(long entryId, IProgress<CopyProgress>? progress = null, CancellationToken cancellationToken = default, bool deferVerification = false)` and pass `deferVerification` into `StartAttempt(entry, progress, cancellationToken, deferVerification)` (add the parameter there).
2. Replace
```csharp
            if (open is { Phase: AttemptPhase.Published or AttemptPhase.Verified })
                return FinishPublished(entry, open, progress, cancellationToken);
```
with
```csharp
            if (open is { Phase: AttemptPhase.Published or AttemptPhase.Verified })
                return deferVerification && NeedsVerification(entry, open)
                    ? new CopyResult(CopyOutcome.Published)
                    : FinishPublished(entry, open, progress, cancellationToken);
```
3. At the end of `StartAttempt`, replace
```csharp
        return FinishPublished(_journal.GetEntry(entry.Id), _journal.GetOpenAttempt(entry.Id)!, progress, cancellationToken);
```
with
```csharp
        var published = _journal.GetEntry(entry.Id);
        var attempt = _journal.GetOpenAttempt(entry.Id)!;
        // Deferred: the file is safely at its final name; the job verifies it later, without the source drive.
        if (deferVerification && NeedsVerification(published, attempt)) return new CopyResult(CopyOutcome.Published);
        return FinishPublished(published, attempt, progress, cancellationToken);
```
4. In `FinishPublished`, replace the `bool needsVerification = ...;` line with `bool needsVerification = NeedsVerification(entry, attempt);` and add:
```csharp
    /// A published copy still has to be read back unless it has no hash to check or was verified before publishing.
    private static bool NeedsVerification(Entry entry, Attempt attempt) =>
        attempt.Phase == AttemptPhase.Published && entry.HashAlgorithm is not null && entry.DestHash is null;
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test`
Expected: PASS — all tests (crash matrix unchanged), 5 new.

- [ ] **Step 5: Commit**

```bash
git add src/Squeue.Core/Copy/CopyTypes.cs src/Squeue.Core/Copy/FileCopier.cs tests/Squeue.Tests/Copy/DeferredVerificationTests.cs
git commit -m "feat: publish now, verify later" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Disks and free space

**Files:**
- Create: `src/Squeue.Core/FileSystem/Volumes.cs`, `src/Squeue.Core/Jobs/Capacity.cs`
- Test: `tests/Squeue.Tests/FileSystem/VolumeTests.cs`, `tests/Squeue.Tests/Jobs/CapacityTests.cs`

**Interfaces:**
- Produces (namespace `Squeue.Core.FileSystem`): `interface IDiskTopology { IReadOnlyList<string> DisksOf(string path); }` — disk keys for the path's volume (`disk:<n>`, `net:<SERVER>`, `unk:<volume>`), empty when the volume isn't there; `readonly record struct VolumeSpace(string Root, long FreeBytes, long TotalBytes, long ClusterSize)`; `interface IVolumeSpace { VolumeSpace? Query(string path); }` (null when the volume isn't there); `sealed class WindowsDiskTopology : IDiskTopology`; `sealed class WindowsVolumeSpace : IVolumeSpace`.
- Produces (namespace `Squeue.Core.Jobs`): `static class Capacity { long RoundUp(long size, long cluster); long Need(IEnumerable<long> sizes, long cluster); long Headroom(long totalBytes); string? Problem(VolumeSpace space, long need, long reservedByRunningJobs); string Text(long bytes); }`.

- [ ] **Step 1: Write the failing tests**

`tests/Squeue.Tests/FileSystem/VolumeTests.cs`:

```csharp
using Squeue.Core.FileSystem;

namespace Squeue.Tests.FileSystem;

public class VolumeTests
{
    private static string MissingDrive() =>
        Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Reverse().First(c => !Directory.Exists($"{c}:\\")) + @":\";

    [Fact]
    public void Paths_on_one_volume_map_to_the_same_disks()
    {
        using var dir = new TestDir();
        var topology = new WindowsDiskTopology();

        var a = topology.DisksOf(dir.Path);
        var b = topology.DisksOf(dir.PathOf(@"not\created\yet"));

        Assert.NotEmpty(a);
        Assert.Equal(a, b);
        Assert.All(a, d => Assert.Matches("^(disk:\\d+|unk:.+)$", d));
    }

    [Fact]
    public void A_missing_drive_has_no_disks() =>
        Assert.Empty(new WindowsDiskTopology().DisksOf(MissingDrive() + "photos"));

    [Fact]
    public void A_network_share_is_one_disk_per_server() =>
        Assert.Equal(new[] { "net:NAS" }, new WindowsDiskTopology().DisksOf(@"\\nas\photos\2026"));

    [Fact]
    public void Free_space_is_read_for_a_folder()
    {
        using var dir = new TestDir();
        var space = new WindowsVolumeSpace().Query(dir.PathOf(@"not\created\yet"))!.Value;

        Assert.True(space.FreeBytes > 0 && space.TotalBytes >= space.FreeBytes);
        Assert.True(space.ClusterSize >= 512 && (space.ClusterSize & (space.ClusterSize - 1)) == 0);
        Assert.Equal(Path.GetPathRoot(dir.Path), space.Root, ignoreCase: true);
    }

    [Fact]
    public void A_missing_drive_has_no_free_space() =>
        Assert.Null(new WindowsVolumeSpace().Query(MissingDrive() + "photos"));
}
```

`tests/Squeue.Tests/Jobs/CapacityTests.cs`:

```csharp
using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;

namespace Squeue.Tests.Jobs;

public class CapacityTests
{
    private const long GB = 1L << 30;

    [Theory]
    [InlineData(0, 4096, 0)]
    [InlineData(1, 4096, 4096)]
    [InlineData(4096, 4096, 4096)]
    [InlineData(4097, 4096, 8192)]
    public void Files_take_whole_clusters(long size, long cluster, long expected) =>
        Assert.Equal(expected, Capacity.RoundUp(size, cluster));

    [Fact]
    public void Need_adds_up_rounded_sizes() =>
        Assert.Equal(12288, Capacity.Need([1, 4096, 4097], 4096));

    [Fact]
    public void Headroom_is_1_GB_or_1_percent() =>
        Assert.Equal((GB, 20 * GB), (Capacity.Headroom(50 * GB), Capacity.Headroom(2000 * GB)));

    [Fact]
    public void A_job_that_fits_has_no_problem() =>
        Assert.Null(Capacity.Problem(new VolumeSpace(@"F:\", 10 * GB, 100 * GB, 4096), 8 * GB, 0));

    [Fact]
    public void Headroom_and_running_jobs_count_against_free_space()
    {
        var space = new VolumeSpace(@"F:\", 10 * GB, 100 * GB, 4096);

        string? problem = Capacity.Problem(space, 8 * GB, reservedByRunningJobs: 2 * GB);

        Assert.Equal(@"Not enough space on F:\: this needs 8 GB, and 7 GB is free after keeping 1 GB spare. Free some space and resume.", problem);
    }

    [Fact]
    public void Small_amounts_are_shown_in_MB() =>
        Assert.Equal(("512 MB", "1.5 GB"), (Capacity.Text(512L << 20), Capacity.Text(3 * GB / 2)));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~VolumeTests|FullyQualifiedName~CapacityTests"`
Expected: build FAILS — the types don't exist.

- [ ] **Step 3: Implement**

`src/Squeue.Core/FileSystem/Volumes.cs`:

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Squeue.Core.FileSystem;

/// Which physical disks a path lives on. Runtime topology only: never stored.
public interface IDiskTopology
{
    /// Disk keys ("disk:2", "net:NAS", "unk:\\?\Volume{…}\") for the path's volume; empty when the volume isn't there.
    IReadOnlyList<string> DisksOf(string path);
}

public readonly record struct VolumeSpace(string Root, long FreeBytes, long TotalBytes, long ClusterSize);

public interface IVolumeSpace
{
    /// Free space, size and cluster size of the path's volume; null when the volume isn't there.
    VolumeSpace? Query(string path);
}

public sealed class WindowsDiskTopology : IDiskTopology
{
    public IReadOnlyList<string> DisksOf(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) && !full.StartsWith(@"\\?\", StringComparison.Ordinal))
            return ["net:" + full[2..].Split('\\')[0].ToUpperInvariant()];

        if (VolumeNative.VolumePathOf(full) is not { } mount) return [];
        if (VolumeNative.VolumeGuidOf(mount) is not { } volume) return [];
        var disks = VolumeNative.DiskNumbersOf(volume);
        return disks is { Count: > 0 }
            ? disks.Select(n => "disk:" + n).ToList()
            : ["unk:" + volume.ToUpperInvariant()];
    }
}

public sealed class WindowsVolumeSpace : IVolumeSpace
{
    public VolumeSpace? Query(string path)
    {
        string full = Path.GetFullPath(path);
        if (VolumeNative.VolumePathOf(full) is not { } mount || !Directory.Exists(mount)) return null;
        if (!VolumeNative.GetDiskFreeSpaceEx(mount, out ulong free, out ulong total, out _)) return null;
        long cluster = VolumeNative.GetDiskFreeSpace(mount, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _)
            ? (long)sectorsPerCluster * bytesPerSector
            : 4096;
        return new VolumeSpace(mount, (long)free, (long)total, cluster);
    }
}

internal static unsafe partial class VolumeNative
{
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathName(string fileName, char* volumePathName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, char* volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);

    /// The mount point holding the path ("E:\" or a mounted folder), or null. Works for paths that don't exist yet.
    public static string? VolumePathOf(string path)
    {
        char* buffer = stackalloc char[1024];
        return GetVolumePathName(path, buffer, 1024) ? new string(buffer) : null;
    }

    /// "\\?\Volume{…}\" for a mount point, or null when no volume is mounted there (e.g. a removed drive).
    public static string? VolumeGuidOf(string mountPoint)
    {
        char* buffer = stackalloc char[64];
        return GetVolumeNameForVolumeMountPoint(mountPoint, buffer, 64) ? new string(buffer) : null;
    }

    /// Physical disk numbers under a volume, or null when Windows won't say (some virtual or USB enclosures).
    public static IReadOnlyList<int>? DiskNumbersOf(string volumeGuidPath)
    {
        // "\\?\Volume{…}\" → "\\.\Volume{…}" opens the volume itself; no access rights are needed for this query.
        string device = @"\\.\" + volumeGuidPath[4..].TrimEnd('\\');
        using var handle = Native.CreateFile(device, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, 0, Native.OPEN_EXISTING, 0, 0);
        if (handle.IsInvalid) return null;

        const int size = 4096;
        byte* output = stackalloc byte[size];
        if (!DeviceIoControl(handle, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, output, size, out _, 0)) return null;
        // VOLUME_DISK_EXTENTS: DWORD count, padding, then DISK_EXTENT { DWORD DiskNumber; pad; LARGE_INTEGER x2 } (24 bytes).
        int count = *(int*)output;
        var disks = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            int number = *(int*)(output + 8 + i * 24);
            if (!disks.Contains(number)) disks.Add(number);
        }
        return disks;
    }
}
```

(`Native.CreateFile` prepends nothing — it takes the raw device path. If it applies `LongPath`, call the `LibraryImport` directly with the device string; check `Native.cs`.)

`src/Squeue.Core/Jobs/Capacity.cs`:

```csharp
using System.Globalization;
using Squeue.Core.FileSystem;

namespace Squeue.Core.Jobs;

/// Admission check for a job's destination: will everything it still has to write fit, with room to spare?
/// This is admission control, not a guarantee — other programs can still fill the drive.
public static class Capacity
{
    public static long RoundUp(long size, long cluster) =>
        cluster <= 0 ? size : (size + cluster - 1) / cluster * cluster;

    /// Space the files will take: each rounded up to whole clusters. Overwrites get no credit, because the old
    /// file stays until the verified new one replaces it.
    public static long Need(IEnumerable<long> sizes, long cluster) => sizes.Sum(s => RoundUp(s, cluster));

    /// Always leave max(1 GB, 1 % of the volume) free.
    public static long Headroom(long totalBytes) => Math.Max(1L << 30, totalBytes / 100);

    /// Null when the job fits; otherwise a sentence saying what's missing.
    public static string? Problem(VolumeSpace space, long need, long reservedByRunningJobs)
    {
        long headroom = Headroom(space.TotalBytes);
        long available = space.FreeBytes - reservedByRunningJobs - headroom;
        if (available >= need) return null;
        return $"Not enough space on {space.Root}: this needs {Text(need)}, and {Text(Math.Max(0, available))} is free " +
               $"after keeping {Text(headroom)} spare. Free some space and resume.";
    }

    public static string Text(long bytes) => bytes >= 1L << 30
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1073741824.0:0.#} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.#} MB");
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test`
Expected: PASS — all tests, 13 new (5 volume + 8 capacity cases).

- [ ] **Step 5: Commit**

```bash
git add src/Squeue.Core/FileSystem/Volumes.cs src/Squeue.Core/Jobs/Capacity.cs tests/Squeue.Tests/FileSystem/VolumeTests.cs tests/Squeue.Tests/Jobs/CapacityTests.cs
git commit -m "feat: physical disks and free space of a path" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Scheduling pass

**Files:**
- Create: `src/Squeue.Core/Jobs/Scheduler.cs`
- Test: `tests/Squeue.Tests/Jobs/SchedulerTests.cs`

**Interfaces:**
- Produces: `sealed record SchedulerJob(long Id, IReadOnlySet<string> Disks)`; `enum WaitKind { DiskInUse, AheadInQueue }`; `sealed record Wait(WaitKind Kind, long OtherJobId)`; `sealed record SchedulerResult(IReadOnlyList<long> Start, IReadOnlyDictionary<long, Wait> Waiting, IReadOnlyDictionary<long, string> NoSpace)`; `static SchedulerResult Scheduler.Pass(IReadOnlyList<SchedulerJob> queue, IReadOnlyDictionary<string, long> busy, Func<SchedulerJob, string?> capacityProblem)` — `queue` in queue order; `busy` maps a held disk to the running job holding it.

- [ ] **Step 1: Write the failing tests**

`tests/Squeue.Tests/Jobs/SchedulerTests.cs`:

```csharp
using Squeue.Core.Jobs;

namespace Squeue.Tests.Jobs;

public class SchedulerTests
{
    private static SchedulerJob Job(long id, params string[] disks) => new(id, disks.ToHashSet());
    private static readonly Dictionary<string, long> Idle = [];
    private static string? Fits(SchedulerJob _) => null;

    [Fact]
    public void Jobs_on_different_disks_all_start()
    {
        var r = Scheduler.Pass([Job(1, "card", "hdd"), Job(2, "usb", "ssd")], Idle, Fits);
        Assert.Equal(new long[] { 1, 2 }, r.Start);
    }

    [Fact]
    public void A_job_sharing_a_disk_with_one_started_this_pass_waits()
    {
        var r = Scheduler.Pass([Job(1, "card", "hdd"), Job(2, "card", "ssd")], Idle, Fits);
        Assert.Equal(new long[] { 1 }, r.Start);
        Assert.Equal(new Wait(WaitKind.DiskInUse, 1), r.Waiting[2]);
    }

    [Fact]
    public void A_job_whose_disk_is_held_by_a_running_job_waits()
    {
        var r = Scheduler.Pass([Job(1, "card", "hdd")], new Dictionary<string, long> { ["hdd"] = 9 }, Fits);
        Assert.Empty(r.Start);
        Assert.Equal(new Wait(WaitKind.DiskInUse, 9), r.Waiting[1]);
    }

    [Fact]
    public void A_later_job_never_overtakes_an_earlier_one_on_a_shared_disk()
    {
        // Job 1 waits for the busy HDD; job 2 only needs the card and an idle SSD, but job 1 is ahead of it on the card.
        var r = Scheduler.Pass([Job(1, "card", "hdd"), Job(2, "card", "ssd")], new Dictionary<string, long> { ["hdd"] = 9 }, Fits);
        Assert.Empty(r.Start);
        Assert.Equal(new Wait(WaitKind.AheadInQueue, 1), r.Waiting[2]);
    }

    [Fact]
    public void A_job_sharing_nothing_with_a_blocked_job_starts()
    {
        var r = Scheduler.Pass([Job(1, "card", "hdd"), Job(3, "usb", "ssd")], new Dictionary<string, long> { ["hdd"] = 9 }, Fits);
        Assert.Equal(new long[] { 3 }, r.Start);
    }

    [Fact]
    public void A_card_released_by_a_verifying_job_starts_the_next_job()
    {
        // Job 9 is verifying: it holds only its destination HDD.
        var r = Scheduler.Pass([Job(2, "card", "ssd")], new Dictionary<string, long> { ["hdd"] = 9 }, Fits);
        Assert.Equal(new long[] { 2 }, r.Start);
    }

    [Fact]
    public void A_reverse_direction_job_waits_for_the_verifying_job()
    {
        var r = Scheduler.Pass([Job(2, "hdd", "card")], new Dictionary<string, long> { ["hdd"] = 9 }, Fits);
        Assert.Equal(new Wait(WaitKind.DiskInUse, 9), r.Waiting[2]);
    }

    [Fact]
    public void A_job_without_space_is_reported_and_does_not_block_later_jobs()
    {
        var r = Scheduler.Pass([Job(1, "card", "hdd"), Job(2, "card", "ssd")], Idle, j => j.Id == 1 ? "full" : null);
        Assert.Equal("full", r.NoSpace[1]);
        Assert.Equal(new long[] { 2 }, r.Start);
    }

    [Fact]
    public void The_blocker_is_the_same_whatever_the_set_order()
    {
        var busy = new Dictionary<string, long> { ["a"] = 7, ["z"] = 8 };
        Assert.Equal(7, Scheduler.Pass([Job(1, "z", "a")], busy, Fits).Waiting[1].OtherJobId);
        Assert.Equal(7, Scheduler.Pass([Job(1, "a", "z")], busy, Fits).Waiting[1].OtherJobId);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SchedulerTests`
Expected: build FAILS — `Scheduler` doesn't exist.

- [ ] **Step 3: Implement**

`src/Squeue.Core/Jobs/Scheduler.cs`:

```csharp
namespace Squeue.Core.Jobs;

/// A queued job as the scheduler sees it: the disks its next phase needs.
public sealed record SchedulerJob(long Id, IReadOnlySet<string> Disks);

public enum WaitKind
{
    /// A running job (or one started earlier in this pass) holds one of its disks.
    DiskInUse,

    /// An earlier queued job that is itself waiting shares one of its disks.
    AheadInQueue,
}

public sealed record Wait(WaitKind Kind, long OtherJobId);

public sealed record SchedulerResult(
    IReadOnlyList<long> Start,
    IReadOnlyDictionary<long, Wait> Waiting,
    IReadOnlyDictionary<long, string> NoSpace);

/// One scheduling pass. A pure function, so its rules are table-tested:
/// a job starts only when all its disks are free; a job never starts ahead of an earlier queued job that shares
/// any of its disks; jobs that share no disk with a blocked job start as soon as their own disks are free.
public static class Scheduler
{
    public static SchedulerResult Pass(IReadOnlyList<SchedulerJob> queue, IReadOnlyDictionary<string, long> busy,
        Func<SchedulerJob, string?> capacityProblem)
    {
        var holders = new Dictionary<string, (long Job, bool Holds)>(StringComparer.Ordinal);
        foreach (var (disk, job) in busy) holders[disk] = (job, true);
        var start = new List<long>();
        var waiting = new Dictionary<long, Wait>();
        var noSpace = new Dictionary<long, string>();

        foreach (var job in queue)
        {
            var disks = job.Disks.Order(StringComparer.Ordinal).ToList();
            (long Job, bool Holds)? blocker = null;
            foreach (string disk in disks)
            {
                if (holders.TryGetValue(disk, out var h)) { blocker = h; break; }
            }

            if (blocker is { } b)
            {
                waiting[job.Id] = new Wait(b.Holds ? WaitKind.DiskInUse : WaitKind.AheadInQueue, b.Job);
                // Its disks now belong to it for the rest of this pass, so no later job overtakes it on them.
                foreach (string disk in disks) holders.TryAdd(disk, (job.Id, false));
                continue;
            }

            // A job that can't fit is paused by the caller; it leaves the queue, so it blocks nothing.
            if (capacityProblem(job) is { } problem)
            {
                noSpace[job.Id] = problem;
                continue;
            }

            foreach (string disk in disks) holders[disk] = (job.Id, true);
            start.Add(job.Id);
        }

        return new SchedulerResult(start, waiting, noSpace);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test`
Expected: PASS — all tests, 9 new.

- [ ] **Step 5: Commit**

```bash
git add src/Squeue.Core/Jobs/Scheduler.cs tests/Squeue.Tests/Jobs/SchedulerTests.cs
git commit -m "feat: scheduling pass with strict per-disk order" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The coordinator and job workers

**Files:**
- Create: `src/Squeue.Core/Jobs/JobProgress.cs`, `src/Squeue.Core/Jobs/JobWorker.cs`
- Replace: `src/Squeue.Core/Jobs/JobRunner.cs`
- Modify: `src/Squeue.Core/Jobs/JobQueue.cs`, `tests/Squeue.Tests/Jobs/RunnerHarness.cs`
- Create: `tests/Squeue.Tests/Fakes/FolderTopology.cs`, `tests/Squeue.Tests/Fakes/FakeVolumeSpace.cs`, `tests/Squeue.Tests/Fakes/GateFileSystem.cs`
- Test: `tests/Squeue.Tests/Jobs/MultiDriveTests.cs` (all existing `JobRunnerTests` must pass unchanged)

**Interfaces:**
- Consumes: Tasks 1–4.
- Produces: `JobSnapshot` gains a last positional parameter `string? WaitingFor = null` (why a queued job hasn't started); while `State == Verifying`, `TotalBytes`/`DoneBytes` describe the verification pass. `JobRunner(IFileSystem fs, string journalPath, JobRunnerOptions? options = null, IDiskTopology? topology = null, IVolumeSpace? space = null)`. `IJobQueue` unchanged apart from the event doc ("raised on a background thread").

- [ ] **Step 1: Test fakes and harness**

`tests/Squeue.Tests/Fakes/FolderTopology.cs`:

```csharp
using Squeue.Core.FileSystem;

namespace Squeue.Tests.Fakes;

/// Pretends folders are on different disks: the last path segment named in the map decides the disk.
public sealed class FolderTopology(params (string Folder, string Disk)[] map) : IDiskTopology
{
    public IReadOnlyList<string> DisksOf(string path)
    {
        var segments = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            foreach (var (folder, disk) in map)
            {
                if (string.Equals(segments[i], folder, StringComparison.OrdinalIgnoreCase)) return [disk];
            }
        }
        return ["other"];
    }
}
```

`tests/Squeue.Tests/Fakes/FakeVolumeSpace.cs`:

```csharp
using Squeue.Core.FileSystem;

namespace Squeue.Tests.Fakes;

public sealed class FakeVolumeSpace(long free, long total = 1L << 40, long cluster = 4096) : IVolumeSpace
{
    public VolumeSpace? Query(string path) => new VolumeSpace(Path.GetPathRoot(Path.GetFullPath(path))!, free, total, cluster);
}
```

`tests/Squeue.Tests/Fakes/GateFileSystem.cs`:

```csharp
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
```

(If `IFileSystem` has other members, pass them through; the build will say.)

`RunnerHarness`: change the constructor to `public RunnerHarness(IFileSystem? fs = null, IDiskTopology? topology = null, IVolumeSpace? space = null)`, store `topology`/`space` in fields, and in `NewRunner` use `new JobRunner(Fs, JournalPath, Options, _topology, _space)`. Add `using Squeue.Core.FileSystem;` if missing.

- [ ] **Step 2: Write the failing tests**

`tests/Squeue.Tests/Jobs/MultiDriveTests.cs`:

```csharp
using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;
using Squeue.Core.State;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Jobs;

public class MultiDriveTests
{
    // Folder name → pretend disk. cardA1 and cardA2 are two folders on the same card.
    private static FolderTopology Topology() => new(
        ("cardA1", "card-a"), ("cardA2", "card-a"), ("cardB", "card-b"),
        ("hddA", "hdd-a"), ("hddB", "hdd-b"), ("ssd", "ssd"));

    private static JobPlan Plan(RunnerHarness h, string source, string dest)
    {
        h.Dir.Write(Path.Combine(source, $"{source}.bin"), TestDir.RandomBytes(20_000, source.Length));
        return JobPlanner.Plan(h.Fs, [h.Dir.PathOf(source)], h.Dir.PathOf(dest));
    }

    [Fact]
    public void Jobs_on_different_drives_run_at_the_same_time()
    {
        var gates = new GateFileSystem(new WindowsFileSystem());
        using var h = new RunnerHarness(gates, Topology());
        var a = Plan(h, "cardA1", "hddA");
        var b = Plan(h, "cardB", "hddB");
        var holdA = gates.HoldSourcesUnder(h.Dir.PathOf("cardA1"));
        try
        {
            h.Runner.Start();
            h.Runner.Enqueue(a, OverwritePolicy.Replace, verify: true);
            var first = h.WaitFor(s => s.Name == "cardA1" && s.State == JobState.Running);
            h.Runner.Enqueue(b, OverwritePolicy.Replace, verify: true);

            h.WaitFor(s => s.Name == "cardB" && s.State == JobState.Done);

            Assert.DoesNotContain(h.Events, s => s.Id == first.Id && s.State == JobState.Done);
        }
        finally { holdA.Set(); }
        h.WaitFor(s => s.Name == "cardA1" && s.State == JobState.Done);
    }

    [Fact]
    public void Jobs_sharing_a_drive_run_in_queue_order_and_say_why_they_wait()
    {
        var gates = new GateFileSystem(new WindowsFileSystem());
        using var h = new RunnerHarness(gates, Topology());
        var a = Plan(h, "cardA1", "hddA");
        var b = Plan(h, "cardA2", "ssd");
        var holdA = gates.HoldSourcesUnder(h.Dir.PathOf("cardA1"));
        try
        {
            h.Runner.Start();
            h.Runner.Enqueue(a, OverwritePolicy.Replace, verify: false);
            h.WaitFor(s => s.Name == "cardA1" && s.State == JobState.Running);
            h.Runner.Enqueue(b, OverwritePolicy.Replace, verify: false);

            var waiting = h.WaitFor(s => s.Name == "cardA2" && s.WaitingFor != null);

            Assert.Equal(JobState.Queued, waiting.State);
            Assert.Contains("cardA1", waiting.WaitingFor);
        }
        finally { holdA.Set(); }

        h.WaitFor(s => s.Name == "cardA2" && s.State == JobState.Done);
        var events = h.Events;
        int aDone = Array.FindIndex(events, s => s.Name == "cardA1" && s.State == JobState.Done);
        int bRunning = Array.FindIndex(events, s => s.Name == "cardA2" && s.State == JobState.Running);
        Assert.True(aDone >= 0 && bRunning > aDone, "The second job started before the first one finished with the card.");
    }

    [Fact]
    public void The_card_is_free_while_the_previous_job_verifies()
    {
        var gates = new GateFileSystem(new WindowsFileSystem());
        using var h = new RunnerHarness(gates, Topology());
        var a = Plan(h, "cardA1", "hddA");
        var b = Plan(h, "cardA2", "ssd");
        var holdVerify = gates.HoldVerificationUnder(h.Dir.PathOf("hddA"));
        try
        {
            h.Runner.Start();
            h.Runner.Enqueue(a, OverwritePolicy.Replace, verify: true);
            var first = h.WaitFor(s => s.Name == "cardA1" && s.State == JobState.Verifying);
            h.Runner.Enqueue(b, OverwritePolicy.Replace, verify: true);

            h.WaitFor(s => s.Name == "cardA2" && s.State == JobState.Done);

            Assert.DoesNotContain(h.Events, s => s.Id == first.Id && s.State == JobState.Done);
        }
        finally { holdVerify.Set(); }
        var done = h.WaitFor(s => s.Name == "cardA1" && s.State == JobState.Done);
        Assert.Equal((1, 0), (done.DoneFiles, done.FailedFiles));
    }

    [Fact]
    public void Pausing_one_of_two_running_jobs_leaves_the_other_running()
    {
        var gates = new GateFileSystem(new WindowsFileSystem());
        using var h = new RunnerHarness(gates, Topology());
        var a = Plan(h, "cardA1", "hddA");
        var b = Plan(h, "cardB", "hddB");
        var holdA = gates.HoldSourcesUnder(h.Dir.PathOf("cardA1"));
        var holdB = gates.HoldSourcesUnder(h.Dir.PathOf("cardB"));
        try
        {
            h.Runner.Start();
            h.Runner.Enqueue(a, OverwritePolicy.Replace, verify: true);
            h.Runner.Enqueue(b, OverwritePolicy.Replace, verify: true);
            var first = h.WaitFor(s => s.Name == "cardA1" && s.State == JobState.Running);
            h.WaitFor(s => s.Name == "cardB" && s.State == JobState.Running);

            h.Runner.Pause(first.Id);
            holdA.Set();
            h.WaitFor(s => s.Id == first.Id && s.State == JobState.Paused);
            holdB.Set();

            h.WaitFor(s => s.Name == "cardB" && s.State == JobState.Done);
        }
        finally { holdA.Set(); holdB.Set(); }
        Assert.Empty(Directory.Exists(h.Dir.PathOf("hddA")) ? Directory.GetFiles(h.Dir.PathOf("hddA"), "~tq*.tmp", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public void A_destination_without_enough_space_pauses_the_job()
    {
        using var h = new RunnerHarness(space: new FakeVolumeSpace(free: 1L << 20));
        var plan = Plan(h, "cardA1", "hddA");
        h.Runner.Start();

        h.Runner.Enqueue(plan, OverwritePolicy.Replace, verify: true);
        var paused = h.WaitFor(s => s.State == JobState.Paused);

        Assert.StartsWith("Not enough space on", paused.LastError);
        Assert.Equal(0, paused.DoneFiles);
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~MultiDriveTests`
Expected: build FAILS — the harness/runner parameters and `WaitingFor` don't exist.

- [ ] **Step 4: Implement**

`src/Squeue.Core/Jobs/JobQueue.cs`: add `string? WaitingFor = null` as the last parameter of `JobSnapshot` with a doc line `/// While Verifying, TotalBytes and DoneBytes describe the verification pass. WaitingFor says why a queued job hasn't started.` above the record; change the `JobChanged` doc to `/// Raised on a background thread (the coordinator's or a job's), never the caller's.`

`src/Squeue.Core/Jobs/JobProgress.cs` (moved out of `JobRunner`, with `DriveProblems` holding the drive helpers that `JobRunner` has today — `IsDeviceTrouble`, `MissingVolume`, `NotConnected`, `DeviceReason` — unchanged in behaviour):

```csharp
using Squeue.Core.State;

namespace Squeue.Core.Jobs;

/// File and byte counts of a job, from its journal entries.
internal sealed class JobCounters
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

internal static class Snapshots
{
    public static JobSnapshot Create(JobRow job, JobState state, JobCounters c, long doneBytes, long totalBytes,
        string? currentFile, double speed, string? lastError, string? waitingFor) =>
        new(job.Id, job.Name ?? "Copy", job.Source ?? "", job.DestRoot ?? "", state,
            c.Total, c.DoneCount, c.FailedCount, totalBytes, doneBytes, currentFile, speed, lastError,
            DateTime.FromFileTimeUtc(job.CreatedAt), waitingFor);
}

/// Recognising a drive problem (as opposed to a bad file) and saying it in plain words.
internal static class DriveProblems
{
    /// Not ready, network name gone, device not connected (433, 1167), disk full (112, 39).
    public static bool IsDeviceTrouble(int code) => code is 21 or 55 or 433 or 1167 or 112 or 39;

    /// The root of the job's source or destination when that drive isn't connected.
    public static string? MissingVolume(JobRow job)
    {
        foreach (string? path in new[] { job.Source, job.DestRoot })
        {
            if (string.IsNullOrEmpty(path)) continue;
            string? root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root)) return root;
        }
        return null;
    }

    public static string NotConnected(string root) => $"{root} isn't connected. Reconnect it and resume.";

    public static string DeviceReason(JobRow job, int code) =>
        MissingVolume(job) is { } root ? NotConnected(root)
        : code is 112 or 39 ? "The destination drive is full. Free some space and resume."
        : "A drive stopped responding. Reconnect it and resume.";
}
```

(Before writing it, compare these helpers with the ones in today's `JobRunner.cs` and keep today's exact texts and codes if they differ.)

`src/Squeue.Core/Jobs/JobWorker.cs`:

```csharp
using System.Diagnostics;
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Core.Jobs;

/// Runs one job on the calling (worker) thread with its own journal connection: first it copies every file that
/// still needs copying (publishing each one, verification deferred), then it verifies every published copy.
/// It never touches the coordinator's state. It publishes progress, calls enteringVerification when it moves on
/// to verifying (the coordinator then frees the source drives), and returns why it paused, or null.
internal sealed class JobWorker
{
    private readonly IFileSystem _fs;
    private readonly Journal _journal;
    private readonly JobRunnerOptions _options;
    private readonly long _jobId;
    private readonly Action<JobSnapshot> _publish;
    private readonly Action _enteringVerification;
    private readonly CancellationToken _stop;
    private readonly Stopwatch _sincePublish = Stopwatch.StartNew();
    private readonly Stopwatch _clock = new();
    private JobRow _job = null!;
    private JobCounters _counters = null!;
    private JobState _state = JobState.Running;
    private long _barDone, _barTotal, _current, _movedThisStage;
    private string? _currentFile;

    public JobWorker(IFileSystem fs, Journal journal, JobRunnerOptions options, long jobId,
        Action<JobSnapshot> publish, Action enteringVerification, CancellationToken stop)
    {
        _fs = fs;
        _journal = journal;
        _options = options;
        _jobId = jobId;
        _publish = publish;
        _enteringVerification = enteringVerification;
        _stop = stop;
    }

    public string? Run()
    {
        // Attempts this job left open (for example when its drive was missing at startup) are settled first.
        Reconcile();
        _job = _journal.GetJob(_jobId);
        var copier = new FileCopier(_fs, _journal, _options.Copy);
        bool defer = _job.HashAlgorithm is not null;
        var entries = _journal.EntriesOf(_jobId);
        _counters = JobCounters.From(entries);

        // Copy phase.
        long published = entries.Where(e => e.State == EntryState.Active).Sum(e => e.PlannedSize);
        var toCopy = entries.Where(e => e.State == EntryState.Pending).ToList();
        if (toCopy.Count > 0)
        {
            StartStage(JobState.Running, _counters.TotalBytes, _counters.FinishedBytes + published);
            foreach (var entry in toCopy)
            {
                if (_stop.IsCancellationRequested) return null;
                var result = CopyOne(copier, entry, defer, allowRetry: true, out string? stopped);
                if (Stops(entry, result, stopped) is { } outcome) return outcome.Reason;
                Count(entry, result);
            }
        }
        if (_stop.IsCancellationRequested) return null;

        // Verification phase: read back every published copy. It needs only the destination drive.
        var toVerify = _journal.EntriesOf(_jobId).Where(e => e.State == EntryState.Active).ToList();
        if (toVerify.Count == 0) return null;
        _enteringVerification();
        StartStage(JobState.Verifying, toVerify.Sum(e => e.PlannedSize), 0);
        foreach (var entry in toVerify)
        {
            if (_stop.IsCancellationRequested) return null;
            // A failed verification leaves the file Pending; the job's next pass re-copies it with the source drive.
            var result = CopyOne(copier, entry, deferVerification: false, allowRetry: false, out string? stopped);
            if (Stops(entry, result, stopped) is { } outcome) return outcome.Reason;
            Count(entry, result);
        }
        return null;
    }

    private sealed record Stop(string? Reason);

    /// Whether this result ends the run: a stop request (null reason), or a drive/journal problem (a reason).
    private Stop? Stops(EntryRow entry, CopyResult result, string? stopped)
    {
        if (result.Outcome == CopyOutcome.Cancelled) return new Stop(null);
        if (stopped is not null)
        {
            // A drive or the journal misbehaved: not the file's fault. Settle this job's attempts and pause.
            Reconcile();
            return new Stop(MissingVolumeReason() ?? stopped);
        }
        if (result.Outcome == CopyOutcome.Failed && (DriveProblems.IsDeviceTrouble(result.Win32Error) || MissingVolumeReason() is not null))
        {
            // The drive failed, not the file: put the file back in line (its attempt is already closed) and pause.
            _journal.SetEntryState(entry.Id, EntryState.Pending);
            return new Stop(DriveProblems.DeviceReason(_job, result.Win32Error));
        }
        return null;
    }

    private void Count(EntryRow entry, CopyResult result)
    {
        if (result.Outcome == CopyOutcome.Done) _counters.Done(entry.PlannedSize);
        else if (result.Outcome == CopyOutcome.Failed) _counters.Failed(entry.PlannedSize, result.Message);
        _barDone += entry.PlannedSize;
        _movedThisStage += entry.PlannedSize;
        _current = 0;
        Publish(force: false);
    }

    private CopyResult CopyOne(FileCopier copier, EntryRow entry, bool deferVerification, bool allowRetry, out string? stopped)
    {
        stopped = null;
        _currentFile = Path.GetFileName(entry.SrcPath);
        _current = 0;
        var progress = new InlineProgress(OnProgress);
        try
        {
            var result = copier.CopyEntry(entry.Id, progress, _stop, deferVerification);
            if (result.Outcome == CopyOutcome.RetryNeeded && allowRetry)
                result = copier.CopyEntry(entry.Id, progress, _stop, deferVerification);

            if (result.Outcome == CopyOutcome.DestinationChanged)
            {
                // There is no per-file conflict prompt yet: keep the other file and report this one.
                const string message = "A file with this name appeared at the destination while copying. It was kept.";
                _journal.SetEntryState(entry.Id, EntryState.Failed, message);
                return new CopyResult(CopyOutcome.Failed, message);
            }
            if (result.Outcome == CopyOutcome.RetryNeeded && allowRetry)
            {
                _journal.SetEntryState(entry.Id, EntryState.Failed, result.Message);
                return new CopyResult(CopyOutcome.Failed, result.Message);
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopped = ex.Message;
            return new CopyResult(CopyOutcome.Failed, ex.Message);
        }
    }

    private void Reconcile()
    {
        try { new Reconciler(_fs, _journal).Run(entry => entry.JobId == _jobId); }
        catch (Exception ex) { Trace.TraceError($"Recovery for job {_jobId} did not complete: {ex}"); }
    }

    private string? MissingVolumeReason() =>
        DriveProblems.MissingVolume(_job) is { } root ? DriveProblems.NotConnected(root) : null;

    private void StartStage(JobState state, long total, long done)
    {
        _state = state;
        _barTotal = total;
        _barDone = done;
        _current = 0;
        _movedThisStage = 0;
        _currentFile = null;
        _clock.Restart();
        Publish(force: true);
    }

    private void OnProgress(CopyProgress p)
    {
        // Each stage's bar follows its own pass: the copy stage moves it while copying, verification while verifying.
        bool counts = _state == JobState.Verifying ? p.Stage == CopyStage.Verifying : p.Stage == CopyStage.Copying;
        if (counts) _current = p.BytesDone;
        Publish(force: false);
    }

    private void Publish(bool force)
    {
        // Once asked to stop, the coordinator reports the job's state after this worker has stopped.
        if (_stop.IsCancellationRequested) return;
        if (!force && _sincePublish.Elapsed < _options.ProgressInterval) return;
        _sincePublish.Restart();
        double seconds = _clock.Elapsed.TotalSeconds;
        double speed = seconds > 0.2 ? (_movedThisStage + _current) / seconds : 0;
        _publish(Snapshots.Create(_job, _state, _counters, _barDone + _current, _barTotal, _currentFile, speed,
            _counters.LastError, null));
    }

    private sealed class InlineProgress(Action<CopyProgress> report) : IProgress<CopyProgress>
    {
        public void Report(CopyProgress value) => report(value);
    }
}
```

Replace `src/Squeue.Core/Jobs/JobRunner.cs` with (keep today's `AddJob`, `DifferentFileKept` and `Publish` exactly as they are — copy them from the current file where marked):

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Core.Jobs;

/// The coordinator. One coordinator thread owns the queue, the disks each running job holds and every scheduling
/// decision; each running job has its own worker thread and journal connection. A job starts only when all the
/// disks its next phase needs are free, so jobs on different disks run in parallel and jobs sharing a disk run in
/// queue order. A copy job moving on to verification frees its source disks. Commands can be called from any
/// thread; they take effect on the coordinator thread, and a running job's disks are released only after its
/// worker has stopped.
public sealed class JobRunner : IJobQueue
{
    private readonly IFileSystem _fs;
    private readonly string _journalPath;
    private readonly JobRunnerOptions _options;
    private readonly IDiskTopology _topology;
    private readonly IVolumeSpace _space;
    private readonly BlockingCollection<Action> _commands = new();
    private readonly object _gate = new();
    private readonly Dictionary<long, CancellationTokenSource> _stops = []; // under _gate
    private Thread? _thread;
    private bool _disposed;

    // Coordinator thread only.
    private Journal? _journal;
    private readonly Dictionary<long, Worker> _workers = [];
    private readonly Dictionary<long, string> _jobErrors = [];
    private readonly Dictionary<long, string> _waiting = [];
    private long _nextRunId;

    public JobRunner(IFileSystem fs, string journalPath, JobRunnerOptions? options = null,
        IDiskTopology? topology = null, IVolumeSpace? space = null)
    {
        _fs = fs;
        _journalPath = journalPath;
        _options = options ?? new JobRunnerOptions();
        _topology = topology ?? new WindowsDiskTopology();
        _space = space ?? new WindowsVolumeSpace();
    }

    public event Action<JobSnapshot>? JobChanged;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) throw new InvalidOperationException("The runner is already started.");
            _thread = new Thread(Run) { IsBackground = true, Name = "Squeue coordinator" };
            _thread.Start();
        }
    }

    public void Enqueue(JobPlan plan, OverwritePolicy policy, bool verify) =>
        Post(() => AddJob(plan, policy, verify));

    public void Pause(long jobId)
    {
        StopIfRunning(jobId);
        Post(() => ChangeState(jobId, JobState.Paused, JobState.Queued, JobState.Running, JobState.Verifying));
    }

    public void Resume(long jobId) =>
        Post(() => ChangeState(jobId, JobState.Queued, JobState.Paused));

    public void Cancel(long jobId)
    {
        StopIfRunning(jobId);
        Post(() => ChangeState(jobId, JobState.Cancelled, JobState.Queued, JobState.Running, JobState.Verifying, JobState.Paused));
    }

    /// Stops every running job (its current file is cleaned up or left to finish next launch) and ends the
    /// coordinator, waiting at most ShutdownTimeout.
    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var stop in _stops.Values) stop.Cancel();
            thread = _thread;
        }
        _commands.CompleteAdding();
        if (thread is not null && !thread.Join(_options.ShutdownTimeout))
            Trace.TraceWarning("The job coordinator did not stop in time; it is left to end with the process.");
    }

    /// A command sent during or after shutdown is dropped.
    private void Post(Action command)
    {
        try { _commands.Add(command); }
        catch (InvalidOperationException) { }
    }

    private static void RunCommand(Action command)
    {
        try { command(); }
        catch (Exception ex) { Trace.TraceError($"Job coordinator command failed: {ex}"); }
    }

    private void Run()
    {
        try
        {
            using var journal = Journal.Open(_journalPath);
            _journal = journal;
            try { new Reconciler(_fs, journal).Run(); }
            catch (Exception ex) { Trace.TraceError($"Startup recovery failed: {ex}"); }
            foreach (var job in journal.Jobs())
            {
                // A job that was running or verifying when the app stopped goes back in the queue.
                if (job.State is JobState.Running or JobState.Verifying) journal.SetJobState(job.Id, JobState.Queued);
            }
            // Unfinished jobs, and today's finished ones; older history isn't shown, so launch time stays flat.
            var today = DateTime.Now.Date;
            foreach (var job in journal.Jobs())
            {
                bool finished = job.State is JobState.Done or JobState.Cancelled;
                if (!finished || DateTime.FromFileTimeUtc(job.CreatedAt).ToLocalTime().Date == today) Publish(SnapshotOf(job.Id));
            }

            SchedulePassSafely();
            while (true)
            {
                Action command;
                try { command = _commands.Take(); }
                catch (InvalidOperationException) { break; } // Dispose was called
                RunCommand(command);
                while (_commands.TryTake(out var more)) RunCommand(more);
                SchedulePassSafely();
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"The job coordinator stopped after an unexpected error: {ex}");
        }
        finally
        {
            StopWorkers();
        }
    }

    private void SchedulePassSafely()
    {
        try { SchedulePass(); }
        catch (Exception ex) { Trace.TraceError($"Scheduling failed: {ex}"); }
    }

    private void SchedulePass()
    {
        if (_commands.IsAddingCompleted) return;
        var journal = _journal!;
        var queue = new List<SchedulerJob>();
        var resources = new Dictionary<long, JobResources>();
        var disksOf = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in journal.Jobs())
        {
            if (job.State != JobState.Queued || _workers.ContainsKey(job.Id)) continue;
            var r = ResourcesOf(job, journal.EntriesOf(job.Id), disksOf);
            if (r.MissingRoot is { } missing)
            {
                PauseWithReason(job.Id, DriveProblems.NotConnected(missing));
                continue;
            }
            queue.Add(new SchedulerJob(job.Id, r.Disks));
            resources[job.Id] = r;
        }

        var busy = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var worker in _workers.Values)
        {
            foreach (string disk in worker.Disks) busy[disk] = worker.JobId;
        }

        var result = Scheduler.Pass(queue, busy, job => CapacityProblem(resources[job.Id]));
        foreach (var (jobId, problem) in result.NoSpace) PauseWithReason(jobId, problem);
        foreach (long jobId in result.Start) StartWorker(jobId, resources[jobId]);
        foreach (var (jobId, wait) in result.Waiting)
        {
            string text = WaitingText(wait);
            if (_waiting.TryGetValue(jobId, out var old) && old == text) continue;
            _waiting[jobId] = text;
            Publish(SnapshotOf(jobId));
        }
    }

    private sealed record JobResources(
        IReadOnlySet<string> Disks, IReadOnlySet<string> DestDisks, string? MissingRoot,
        string DestRoot, IReadOnlyList<long> PendingSizes, bool VerifyOnly);

    /// The disks the job's next phase needs: the destination's, plus the source's while files remain to copy.
    /// A job with only published copies left to verify needs just the destination.
    private JobResources ResourcesOf(JobRow job, IReadOnlyList<EntryRow> entries, Dictionary<string, IReadOnlyList<string>> disksOf)
    {
        string dest = job.DestRoot ?? "";
        var pending = entries.Where(e => e.State == EntryState.Pending).ToList();
        bool verifyOnly = pending.Count == 0 && entries.Any(e => e.State == EntryState.Active);

        IReadOnlyList<string> Resolve(string path)
        {
            if (!disksOf.TryGetValue(path, out var disks)) disksOf[path] = disks = _topology.DisksOf(path);
            return disks;
        }

        var destDisks = new HashSet<string>(StringComparer.Ordinal);
        if (dest.Length > 0)
        {
            var d = Resolve(dest);
            if (d.Count == 0) return Missing(Path.GetPathRoot(dest) ?? dest);
            destDisks.UnionWith(d);
        }
        var all = new HashSet<string>(destDisks, StringComparer.Ordinal);
        foreach (string folder in pending.Select(e => Path.GetDirectoryName(e.SrcPath) ?? e.SrcPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var d = Resolve(folder);
            if (d.Count == 0) return Missing(Path.GetPathRoot(folder) ?? folder);
            all.UnionWith(d);
        }
        return new JobResources(all, destDisks, null, dest, pending.Select(e => e.PlannedSize).ToList(), verifyOnly);

        JobResources Missing(string root) => new(new HashSet<string>(), new HashSet<string>(), root, dest, [], false);
    }

    private string? CapacityProblem(JobResources r)
    {
        if (r.PendingSizes.Count == 0 || r.DestRoot.Length == 0) return null;
        if (_space.Query(r.DestRoot) is not { } space) return DriveProblems.NotConnected(Path.GetPathRoot(r.DestRoot) ?? r.DestRoot);
        long need = Capacity.Need(r.PendingSizes, space.ClusterSize);
        long reserved = 0;
        foreach (var worker in _workers.Values)
        {
            // Space a running job has already allocated is gone from free space; count only what it still has to write.
            if (!string.Equals(worker.DestVolume, space.Root, StringComparison.OrdinalIgnoreCase)) continue;
            reserved += Capacity.Need(
                _journal!.EntriesOf(worker.JobId).Where(e => e.State == EntryState.Pending).Select(e => e.PlannedSize),
                space.ClusterSize);
        }
        return Capacity.Problem(space, need, reserved);
    }

    private string WaitingText(Wait wait)
    {
        string other = _journal!.GetJob(wait.OtherJobId).Name ?? "another job";
        return wait.Kind == WaitKind.DiskInUse
            ? $"“{other}” is using the same drive"
            : $"“{other}” is ahead of it on the same drive";
    }

    private sealed class Worker(long jobId, long runId, IReadOnlySet<string> disks, IReadOnlySet<string> destDisks,
        CancellationTokenSource stop, string? destVolume)
    {
        public long JobId { get; } = jobId;
        public long RunId { get; } = runId;
        public IReadOnlySet<string> Disks { get; set; } = disks;
        public IReadOnlySet<string> DestDisks { get; } = destDisks;
        public CancellationTokenSource Stop { get; } = stop;
        public string? DestVolume { get; } = destVolume;
        public Thread? Thread { get; set; }
    }

    private void StartWorker(long jobId, JobResources r)
    {
        var journal = _journal!;
        long runId = ++_nextRunId;
        var stop = new CancellationTokenSource();
        lock (_gate)
        {
            if (_disposed) stop.Cancel();
            _stops[jobId] = stop;
        }
        string? destVolume = r.DestRoot.Length > 0 ? _space.Query(r.DestRoot)?.Root : null;
        var worker = new Worker(jobId, runId, r.Disks, r.DestDisks, stop, destVolume);
        _workers[jobId] = worker;
        _jobErrors.Remove(jobId);
        _waiting.Remove(jobId);
        journal.SetJobState(jobId, r.VerifyOnly ? JobState.Verifying : JobState.Running);
        Publish(SnapshotOf(jobId));
        worker.Thread = new Thread(() => RunWorker(jobId, runId, stop.Token)) { IsBackground = true, Name = $"Squeue job {jobId}" };
        worker.Thread.Start();
    }

    /// Worker thread.
    private void RunWorker(long jobId, long runId, CancellationToken stop)
    {
        string? pauseReason;
        try
        {
            using var journal = Journal.Open(_journalPath);
            pauseReason = new JobWorker(_fs, journal, _options, jobId, Publish,
                () => Post(() => OnVerifying(jobId, runId)), stop).Run();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Job {jobId} stopped after an unexpected error: {ex}");
            pauseReason = stop.IsCancellationRequested ? null : ex.Message;
        }
        Post(() => OnWorkerExited(jobId, runId, pauseReason));
    }

    /// The worker finished copying and is verifying: only the destination's disks stay held.
    private void OnVerifying(long jobId, long runId)
    {
        if (!_workers.TryGetValue(jobId, out var worker) || worker.RunId != runId) return;
        worker.Disks = worker.DestDisks;
        if (_journal!.GetJob(jobId).State == JobState.Running) _journal.SetJobState(jobId, JobState.Verifying);
    }

    /// The worker has stopped: release its disks and settle the job's state.
    private void OnWorkerExited(long jobId, long runId, string? pauseReason)
    {
        if (!_workers.TryGetValue(jobId, out var worker) || worker.RunId != runId) return;
        _workers.Remove(jobId);
        lock (_gate)
        {
            if (_stops.TryGetValue(jobId, out var stop) && stop == worker.Stop) _stops.Remove(jobId);
        }
        worker.Stop.Dispose();

        var journal = _journal!;
        if (journal.GetJob(jobId).State is JobState.Running or JobState.Verifying)
        {
            if (pauseReason is not null)
            {
                _jobErrors[jobId] = pauseReason;
                journal.SetJobState(jobId, JobState.Paused);
            }
            else
            {
                // Files whose verification failed are Pending again: the job re-queues for a retry pass.
                bool finished = journal.EntriesOf(jobId).All(e => e.State is EntryState.Done or EntryState.Failed);
                journal.SetJobState(jobId, finished ? JobState.Done : JobState.Queued);
            }
        }
        Publish(SnapshotOf(jobId));
    }

    private void StopWorkers()
    {
        var clock = Stopwatch.StartNew();
        foreach (var worker in _workers.Values)
        {
            worker.Stop.Cancel();
            var left = _options.ShutdownTimeout - clock.Elapsed;
            if (worker.Thread is not null && (left <= TimeSpan.Zero || !worker.Thread.Join(left)))
                Trace.TraceWarning($"Job {worker.JobId} did not stop in time; it is left to end with the process.");
        }
    }

    private void ChangeState(long jobId, JobState to, params JobState[] from)
    {
        var journal = _journal!;
        if (!from.Contains(journal.GetJob(jobId).State)) return;
        journal.SetJobState(jobId, to);
        if (to == JobState.Queued) _jobErrors.Remove(jobId);
        _waiting.Remove(jobId);
        if (_workers.TryGetValue(jobId, out var worker))
        {
            // Also catches a worker started after the caller's StopIfRunning. Its new state is reported once it has stopped.
            if (to is JobState.Paused or JobState.Cancelled) worker.Stop.Cancel();
            return;
        }
        Publish(SnapshotOf(jobId));
    }

    private void PauseWithReason(long jobId, string reason)
    {
        _jobErrors[jobId] = reason;
        _waiting.Remove(jobId);
        _journal!.SetJobState(jobId, JobState.Paused);
        Publish(SnapshotOf(jobId));
    }

    private void StopIfRunning(long jobId)
    {
        lock (_gate)
        {
            if (_stops.TryGetValue(jobId, out var stop)) stop.Cancel();
        }
    }

    private JobSnapshot SnapshotOf(long jobId)
    {
        var journal = _journal!;
        var job = journal.GetJob(jobId);
        var c = JobCounters.From(journal.EntriesOf(jobId));
        return Snapshots.Create(job, job.State, c, c.FinishedBytes, c.TotalBytes, null, 0,
            _jobErrors.TryGetValue(jobId, out var e) ? e : c.LastError, _waiting.GetValueOrDefault(jobId));
    }

    // AddJob, DifferentFileKept and Publish: copy unchanged from the previous JobRunner.cs.
}
```

Delete from the old file everything that moved (`JobCounters`, `InlineProgress`, the drive helpers, `RunJob`, `CopyOne`, `NextRunnableJob`, `DrainCommands`); `AddJob`, `DifferentFileKept` and `Publish` stay as they are.

- [ ] **Step 5: Run the tests**

Run `dotnet test --filter "FullyQualifiedName~MultiDriveTests|FullyQualifiedName~JobRunnerTests"` three times, then `dotnet test`.
Expected: PASS every time — all tests, 5 new; every existing `JobRunnerTests` case passes unchanged. If one fails, find the cause (for example a test that relied on the old single thread) and fix the code, not the test; if a test's timing assumption is genuinely no longer valid, stop and report it.

- [ ] **Step 6: Commit**

```bash
git add src/Squeue.Core/Jobs/JobQueue.cs src/Squeue.Core/Jobs/JobProgress.cs src/Squeue.Core/Jobs/JobWorker.cs src/Squeue.Core/Jobs/JobRunner.cs tests/Squeue.Tests/Fakes/FolderTopology.cs tests/Squeue.Tests/Fakes/FakeVolumeSpace.cs tests/Squeue.Tests/Fakes/GateFileSystem.cs tests/Squeue.Tests/Jobs/RunnerHarness.cs tests/Squeue.Tests/Jobs/MultiDriveTests.cs
git commit -m "feat: run jobs on different drives at the same time" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Show verifying and waiting in the window

**Files:**
- Modify: `src/Squeue.ViewModels/JobCardViewModel.cs`, `src/Squeue.ViewModels/MainViewModel.cs`
- Test: `tests/Squeue.Tests/ViewModels/ViewModelTests.cs`

**Interfaces:**
- Consumes: `JobState.Verifying`, `JobSnapshot.WaitingFor`.

- [ ] **Step 1: Write the failing tests** (add to `ViewModelTests`; `FakeJobQueue.Snapshot` builds a Running snapshot — use `with` to change it):

```csharp
    [Fact]
    public void A_verifying_job_is_a_live_card()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);

        queue.Raise(FakeJobQueue.Snapshot(state: JobState.Verifying));

        var card = Assert.Single(main.Jobs);
        Assert.Equal("Verifying · 10 MB/s", card.Status);
        Assert.Equal((true, true, true), (card.IsRunning, card.CanPause, card.CanCancel));
        Assert.NotEqual("", card.TimeLeft);
    }

    [Fact]
    public void A_waiting_job_says_what_it_waits_for()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue);

        queue.Raise(FakeJobQueue.Snapshot(state: JobState.Queued, done: 0, speed: 0) with { WaitingFor = "“DCIM” is using the same drive" });

        Assert.Equal("Waiting · “DCIM” is using the same drive", main.Jobs[0].Status);
    }

    [Fact]
    public void While_verifying_only_the_destination_drive_is_busy()
    {
        var queue = new FakeJobQueue();
        var main = NewMain(queue, new(@"E:\", "Card (E:)"), new(@"F:\", "Backup (F:)"));

        queue.Raise(FakeJobQueue.Snapshot(state: JobState.Verifying));

        Assert.Equal(new[] { false, true }, main.Drives.Select(d => d.IsBusy));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ViewModelTests`
Expected: the 3 new tests FAIL (Verifying isn't active; no waiting text).

- [ ] **Step 3: Implement**

`JobCardViewModel.Update`:
- `IsRunning = s.State is JobState.Running or JobState.Verifying;`
- `CanPause = s.State is JobState.Running or JobState.Verifying or JobState.Queued;`
- `CanCancel = s.State is JobState.Queued or JobState.Running or JobState.Verifying or JobState.Paused;`
- `TimeLeft = s.State is JobState.Running or JobState.Verifying ? Format.TimeLeftFor(s.TotalBytes - s.DoneBytes, s.BytesPerSecond) : "";` (keep today's call, just widen the condition).

`JobCardViewModel.StatusText`: add
```csharp
        JobState.Verifying => s.BytesPerSecond > 0 ? $"Verifying · {Format.Speed(s.BytesPerSecond)}" : "Verifying",
```
and change the Queued case to
```csharp
        JobState.Queued => s.WaitingFor is { Length: > 0 } why
            ? $"Waiting · {why}"
            : $"Waiting · {Format.Count(s.TotalFiles, "file")}, {Format.Bytes(s.TotalBytes)}",
```

`MainViewModel`:
- `IsActive`: add `JobState.Verifying`.
- The subtitle speed sum: `s.State is JobState.Running or JobState.Verifying`.
- `UpdateBusyDrives`: replace the `.Where(...).SelectMany(...)` with
```csharp
            .SelectMany(s => s.State switch
            {
                JobState.Running => new[] { Path.GetPathRoot(s.Source), Path.GetPathRoot(s.DestRoot) },
                JobState.Verifying => new[] { Path.GetPathRoot(s.DestRoot) }, // the source drive is free again
                _ => [],
            })
```

- [ ] **Step 4: Run the tests and launch the app**

Run: `dotnet test` → all pass, 3 new. Then, only if no `Squeue.exe` is running (`Get-Process Squeue -ErrorAction SilentlyContinue`), `dotnet build Squeue.slnx` (0 warnings) and smoke-launch `src\Squeue.App\bin\Debug\net10.0-windows\Squeue.exe`: alive after 6 s with window title "Squeue", then stop it. If Squeue is already running, build only `src/Squeue.ViewModels` and say so.

- [ ] **Step 5: Commit**

```bash
git add src/Squeue.ViewModels/JobCardViewModel.cs src/Squeue.ViewModels/MainViewModel.cs tests/Squeue.Tests/ViewModels/ViewModelTests.cs
git commit -m "feat: show verifying and why a job waits" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## What this plan does not cover

| Item | Where |
| --- | --- |
| Reorder, "run now" | Plan 7 — full app |
| "Safe to remove card" | Plan 7 |
| Several files in flight within one job on SSDs | Later, after measuring |
| Resuming automatically when a drive is plugged back in | Plan 7 (device events exist for the drive pills) |
| Destination claims | Needed only with more than one slot per disk |
