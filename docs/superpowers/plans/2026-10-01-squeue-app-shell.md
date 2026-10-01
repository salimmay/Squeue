# Squeue App Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A window a first-time user understands: Queue, History and Settings tabs; a labelled drop area with "how it works"; buttons with words; a history of finished jobs with their failed files and a "Retry failed" action; settings that persist; the app icon; and a one-command build that produces `dist\Squeue\Squeue.exe`.

**Architecture:**
- **Journal** stores a summary on each finished job (file counts, bytes, last error, finish time), so history is listed from the `jobs` table alone. It can list a job's failed files, put them back in line, and prune old finished jobs.
- **Coordinator (`JobRunner`)** writes the summary when a job ends, publishes finished jobs within the history window at startup, prunes older ones, and gains `RetryFailed` and `RequestFailedFiles`.
- **Settings** live in `%LOCALAPPDATA%\Squeue\settings.json` behind `ISettingsStore` (view-model layer; the journal stays on its own threads).
- **View models** add `HistoryItemViewModel`, `SettingsViewModel`, and `MainViewModel.History`/`Settings`/`QueueFinished`; the plan panel takes its defaults from settings.
- **WPF** gets a `TabControl`, the empty-state drop area, labelled buttons, the icon, and the "when everything is done" action.

**Tech Stack:** C# / .NET 10, WPF (Fluent theme), CommunityToolkit.Mvvm, System.Text.Json, xUnit.

**Design reference:** the three-tab mockup approved in conversation on 2026-10-01 (Queue with drop area, drive legend and "How it works"; History grouped by day with result and actions; Settings rows with a title and one-line explanation). Logo: `src/Squeue.App/Assets/squeue.ico` (already generated on this branch, with `squeue-512.png` and `squeue-32.png`).

**Spec:** Squeue — System Design Spec, Revision 3: https://claude.ai/code/artifact/e58e5773-986c-4724-8466-07c85a154be9 — Requirements "History", "End actions", "Errors: retry failed files later".

**Branch:** `feat/app-shell` (from `feat/multi-drive`; the icon files are present but uncommitted — Task 4 commits them).

## Scope

In: everything in Goal. Out (later plans): "Ask me" per-file conflicts (Conflicts plan), "Copy again", Windows toast notifications (need the installer's app identity — the setting offers sound or taskbar flash for now), queue reordering, compact window, installer and Explorer menu.

## Global Constraints

- Journal connections stay single-threaded: only the coordinator and each job's worker touch the journal. View models never open it; they talk to `IJobQueue`.
- `IJobQueue` events are raised on background threads; view models apply them through `post`.
- Plan-1 safety rules are untouched. `RetryFailed` only changes journal rows of a job that is not running.
- A finished job with failed files must stay visible in the Queue tab until dismissed (existing behaviour) **and** appear in History.
- Settings defaults: check files after copying = on; existing files = Skip; when done = play a sound; keep history = 90 days.
- UI copy: sentence case, plain words, no "please"/"successfully"/exclamation marks. Say "check"/"checked" in the UI for verification.
- Build output is warning-free; every existing test keeps passing unchanged unless a step says otherwise.
- Every commit message ends with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (two `-m` flags). Stage explicit paths; never commit `.superpowers/`, `tools/` or `dist/`.

## Review Focus

1. **A job finishes while the History tab is open** → it appears at the top of "Today" without a restart. Task 3 `A_finished_job_appears_in_history`.
2. **Retry failed** on a job whose files failed → those files are copied again; files that were fine are not touched. Task 2 `RetryFailed_copies_only_the_failed_files`.
3. **A corrupt or missing settings file** → defaults are used and the app starts. Task 3 `A_broken_settings_file_falls_back_to_defaults`.
4. **Hundreds of old jobs** → launch stays fast (history from job rows, no entry scans) and jobs older than the setting are removed, never one with unfinished work. Task 1 `Prune_removes_only_old_finished_jobs_without_open_attempts`.
5. **Databases from earlier builds** (no summary columns) → open fine; old finished jobs get their summary filled in once. Task 1 `Opens_a_database_without_job_summaries`, Task 2 `Old_finished_jobs_get_a_summary_at_startup`.

---

### Task 1: Journal — job summaries, failed files, retry, prune

**Files:** Modify `src/Squeue.Core/State/Journal.cs`, `src/Squeue.Core/State/Records.cs`; test `tests/Squeue.Tests/State/JobHistoryJournalTests.cs`.

**Interfaces (produces):**
- `sealed record JobSummary(int TotalFiles, int DoneFiles, int FailedFiles, long TotalBytes, string? LastError)`
- `JobRow` gains two trailing members: `JobSummary? Summary`, `long? FinishedAt` (FILETIME UTC). Existing positional uses keep compiling (add them as the last positional parameters with defaults `= null`).
- `void Journal.FinishJob(long jobId, JobState state, JobSummary summary)` — sets state, summary columns and `finished_at = now` in one statement.
- `void Journal.SetJobState(...)` unchanged, except: setting a state other than Done/Cancelled clears `finished_at` and the summary columns (a retried job is live again).
- `IReadOnlyList<EntryRow> Journal.FailedEntriesOf(long jobId, int limit)` — failed entries by id, at most `limit`.
- `int Journal.RetryFailedEntries(long jobId)` — failed entries → `Pending`, `error = NULL`, `verify_failures = 0`; returns how many.
- `int Journal.PruneFinishedBefore(long filetimeUtc)` — deletes jobs in Done/Cancelled with `finished_at < cutoff` and no open attempt, with their entries and attempts, in one `Atomically`; returns jobs removed.
- Schema: `jobs` gets `total_files INTEGER, done_files INTEGER, failed_files INTEGER, total_bytes INTEGER, last_error TEXT, finished_at INTEGER`; `Migrate()` adds any that are missing.

- [ ] **Step 1: Write the failing tests** (`JobHistoryJournalTests`, each on a fresh `TestDir` journal):
  - `FinishJob_stores_state_summary_and_time`: create job, `FinishJob(id, Done, new(3, 2, 1, 500, "boom"))` → `GetJob` has State Done, `Summary == new JobSummary(3, 2, 1, 500, "boom")`, `FinishedAt > 0`.
  - `A_job_set_back_to_queued_loses_its_summary`: after FinishJob, `SetJobState(id, Queued)` → `Summary` and `FinishedAt` null.
  - `FailedEntriesOf_lists_failed_files_up_to_the_limit`: 3 entries, 2 failed with errors → limit 1 returns the first failed; limit 10 returns both with their errors.
  - `RetryFailedEntries_puts_only_failed_files_back_in_line`: one Done, one Failed (after `RecordVerifyFailure` twice so `verify_failures` is 2) → returns 1; the failed one is Pending with null error and `GetEntry(id).VerifyFailures == 0`; the Done one unchanged.
  - `Prune_removes_only_old_finished_jobs_without_open_attempts`: job A finished (then force `finished_at` old by calling `PruneFinishedBefore(long.MaxValue)` semantics — use a cutoff in the future), job B finished but with an open attempt (`BeginAttempt`), job C Queued → `PruneFinishedBefore(DateTime.UtcNow.AddDays(1).ToFileTimeUtc())` returns 1; `Jobs()` ids are B and C; A's entries are gone (`EntriesOf(A)` empty).
  - `Opens_a_database_without_job_summaries`: create a DB with the previous `jobs` schema (id, hash_algo, name, source, dest_root, state, created_at) and one Done row via raw `SqliteConnection`; `Journal.Open` succeeds, `Jobs().Single().Summary` is null, and `FinishJob` works on it.

- [ ] **Step 2: Run** `dotnet test --filter FullyQualifiedName~JobHistoryJournalTests` → build FAILS (members missing).
- [ ] **Step 3: Implement** as specified above. `ReadJobs` selects the new columns; `Summary` is non-null only when `total_files` is non-null. Keep every write a single statement or inside `Atomically`; no filesystem calls.
- [ ] **Step 4: Run** `dotnet test` → all pass (6 new), no warnings.
- [ ] **Step 5: Commit** `feat: job summaries, failed-file listing, retry and history pruning in the journal`.

---

### Task 2: Coordinator — history, retry failed, failed files

**Files:** Modify `src/Squeue.Core/Jobs/JobQueue.cs`, `src/Squeue.Core/Jobs/JobRunner.cs`, `src/Squeue.Core/Jobs/JobProgress.cs`, `tests/Squeue.Tests/ViewModels/FakeJobQueue.cs`; test `tests/Squeue.Tests/Jobs/JobHistoryTests.cs`.

**Interfaces (produces):**
- `JobSnapshot` gains trailing positional parameters `DateTime? FinishedAtUtc = null, bool Checked = false` (`Checked` = the job verifies its copies, i.e. has a hash algorithm).
- `sealed record FailedFile(string Path, string Reason)`.
- `IJobQueue` gains `void RetryFailed(long jobId);`, `void RequestFailedFiles(long jobId);`, `event Action<long, IReadOnlyList<FailedFile>>? FailedFilesLoaded;` (raised on a background thread; at most 500 files).
- `JobRunnerOptions.HistoryDays` (int, default 90; 0 = keep forever).
- Behaviour:
  - When the coordinator sets a job Done or Cancelled (worker exit, or `ChangeState` to Cancelled on a job with no worker, or a cancelled job's worker exiting) it calls `FinishJob` with a summary computed from `JobCounters`.
  - Snapshots of jobs with a stored summary are built from the summary (no entry scan); `FinishedAtUtc` and `Checked` are always filled.
  - Startup: after recovery, backfill a summary for every Done/Cancelled job that has none (`FinishJob` with its current counters, keeping its state); prune with `HistoryDays` (skip when 0); publish every unfinished job and every finished job that remains (replaces the "today only" rule).
  - `RetryFailed(jobId)`: on the coordinator thread, only if the job is Done or Cancelled, has no worker, and `RetryFailedEntries` returns > 0 → `SetJobState(Queued)`, publish; the normal pass starts it.
  - `RequestFailedFiles(jobId)`: reads `FailedEntriesOf(jobId, 500)` and raises `FailedFilesLoaded(jobId, files)` with `Reason = error ?? "Unknown error"`.
- `FakeJobQueue` implements the new members: records `"RetryFailed {id}"` / `"RequestFailedFiles {id}"`, exposes `RaiseFailedFiles(long, IReadOnlyList<FailedFile>)`, and `Snapshot(...)` gains optional `finished` and `checkedCopies` parameters.

- [ ] **Step 1: Write the failing tests** (`JobHistoryTests`, using `RunnerHarness`; add a `FailedFiles` recorder to the harness: subscribe to `FailedFilesLoaded`, expose `WaitForFailedFiles(long jobId)` with the same 30 s wait pattern as `WaitFor`):
  - `A_finished_job_carries_its_finish_time_and_checked_flag`: run a small verified job → Done snapshot has `FinishedAtUtc` within the last minute and `Checked == true`; a job enqueued with `verify: false` (no existing files) has `Checked == false`.
  - `Finished_jobs_are_listed_again_after_a_restart`: run job to Done, `h.Restart()` → a Done snapshot for it arrives with the same counts and `FinishedAtUtc`.
  - `Old_finished_jobs_get_a_summary_at_startup`: write a Done job with one Done entry directly into the journal (no summary), start → its snapshot shows `TotalFiles == 1`, `DoneFiles == 1`; reopening the journal shows `Summary` set.
  - `Jobs_older_than_the_history_window_are_removed_at_startup`: harness with `HistoryDays = 1`; insert a Done job and set its `finished_at` three days back with a raw SQL UPDATE on a separate connection before `Start` → no snapshot for it (assert via `h.Events` after another small job has run to Done), and it is gone from `Jobs()`.
  - `RetryFailed_copies_only_the_failed_files`: two files; a file with the same name but different content already at the destination for one of them, policy Skip → Done with 1 failed. Delete the conflicting destination file, `RetryFailed(id)` → the job reaches Done with `(DoneFiles, FailedFiles) == (2, 0)`; the first file has exactly one attempt in the journal (not re-copied).
  - `RetryFailed_does_nothing_for_a_job_without_failures`: Done job with 0 failed → no new snapshot with state Queued/Running after `RetryFailed` (run a second marker job to Done to bound the wait).
  - `Failed_files_can_be_requested`: the 1-failed job above → `RequestFailedFiles(id)` → one `FailedFile` whose `Path` ends with the file name and whose `Reason` is "A different file with this name is already there. It was not replaced."
  - `A_cancelled_job_is_kept_in_history`: cancel a queued job (hold its source with `GateFileSystem` behind another job on the same pretend disk, or cancel before `Start` drains — choose the deterministic way) → Cancelled snapshot with `FinishedAtUtc` set.
- [ ] **Step 2: Run** → build FAILS.
- [ ] **Step 3: Implement.** Keep journal use on the coordinator thread; `RetryFailed`/`RequestFailedFiles` go through `Post`. Extend `Snapshots.Create` (or add `Snapshots.FromSummary`) for the new fields; workers pass `Checked` from the job's hash algorithm and `FinishedAtUtc = null`.
- [ ] **Step 4: Run** `JobHistoryTests` + `JobRunnerTests` + `MultiDriveTests` 5 times, then `dotnet test` → all pass (8 new), no warnings.
- [ ] **Step 5: Commit** `feat: job history, retry failed files, failed-file listing`.

---

### Task 3: View models — settings, history, clearer queue

**Files:** Create `src/Squeue.ViewModels/Settings.cs`, `src/Squeue.ViewModels/SettingsViewModel.cs`, `src/Squeue.ViewModels/HistoryItemViewModel.cs`; modify `MainViewModel.cs`, `PlanViewModel.cs`, `JobCardViewModel.cs`; tests `tests/Squeue.Tests/ViewModels/SettingsTests.cs`, `HistoryViewModelTests.cs`, and additions to `ViewModelTests.cs`.

**Interfaces (produces):**
- `enum ExistingFiles { Skip, Replace }`, `enum WhenDone { Sound, Flash, Nothing }`
- `sealed record AppSettings { bool CheckAfterCopying = true; ExistingFiles ExistingFiles = Skip; WhenDone WhenDone = Sound; int HistoryDays = 90; }`
- `interface ISettingsStore { AppSettings Load(); void Save(AppSettings settings); }`; `sealed class JsonSettingsStore(string path) : ISettingsStore` — `Load` returns defaults when the file is missing, unreadable or invalid JSON; `Save` writes indented JSON with enums as strings, creating the folder; a failed save is swallowed and traced.
- `sealed partial class SettingsViewModel(ISettingsStore store)`: observable `CheckAfterCopying`, `ExistingFiles`, `WhenDone`, `HistoryDays`; every change saves; `AppSettings Current`; option lists for the combo boxes as `IReadOnlyList<Choice<T>>` where `sealed record Choice<T>(T Value, string Label)`: ExistingFiles → "Skip it", "Replace it"; WhenDone → "Play a sound", "Flash the taskbar button", "Do nothing"; HistoryDays → (30, "30 days"), (90, "90 days"), (0, "Forever").
- `sealed partial class HistoryItemViewModel`: `Id`, `Title`, `Route` ("{Source} → {DestRoot}"), `Details` ("14:02 · 1,204 files · 38 GB" — local finish time `HH:mm`), `Result` and `NeedsAttention`:
  - Cancelled → "Cancelled"
  - Done, failed > 0 → "{n} failed" (`NeedsAttention = true`)
  - Done, `Checked` → "All checked"
  - Done, not checked → "Copied, not checked"
  - `Reason` = `LastError ?? ""` shown when failed > 0; `DayLabel` ("Today", "Yesterday", or `d MMM yyyy`), `FinishedLocal` for sorting; `CanRetry` (failed > 0); `ObservableCollection<FailedFile> FailedFiles`; `bool ShowsFailedFiles`; commands `RetryFailedCommand` (→ `queue.RetryFailed`), `ToggleFailedFilesCommand` (first open calls `queue.RequestFailedFiles`), `OpenFolderCommand` (→ an `Action<string>` given by the app with `DestRoot`); `void Update(JobSnapshot)`, `void SetFailedFiles(IReadOnlyList<FailedFile>)`.
- `MainViewModel` constructor becomes `(IJobQueue queue, IDriveSource drives, Action<Action> post, ISettingsStore settings, Action<string>? openFolder = null, Func<DateTime>? now = null)`. New members: `SettingsViewModel Settings`; `ObservableCollection<HistoryItemViewModel> History` (newest first; a Done/Cancelled snapshot adds or updates its item; any other state removes it — e.g. after Retry failed); `string HistoryHeader` ("History" or "History · {n} need attention" / "· 1 needs attention"); `bool IsEmpty` (no cards and no pending plan — drives the empty state); `event Action? QueueFinished` raised when the number of active jobs goes from > 0 to 0; `FailedFilesLoaded` routed to the matching history item through `post`. `Subtitle` when idle becomes "Nothing is copying." `ProposePlan` creates the `PlanViewModel` with `Verify = Settings.CheckAfterCopying` and `Overwrite = Settings.ExistingFiles == Replace`.
- `JobCardViewModel`: unchanged API (button labels live in XAML).

- [ ] **Step 1: Write the failing tests:**
  - `SettingsTests`: `Defaults_when_no_file_exists`; `Saved_settings_are_loaded_back` (all four values changed); `A_broken_settings_file_falls_back_to_defaults` (file content `{ not json`); `Changing_a_setting_saves_it` (through `SettingsViewModel`, then a new store on the same path loads it); `Unknown_values_fall_back_to_defaults` (`"WhenDone": "Dance"`).
  - `HistoryViewModelTests` (FakeJobQueue + an in-memory `ISettingsStore` fake): `A_finished_job_appears_in_history` (Done snapshot, finished now → one item, Title/Route, Result "All checked" when `checkedCopies`, "Copied, not checked" otherwise, DayLabel "Today"); `A_job_with_failures_needs_attention` (Result "2 failed", Reason = LastError, `HistoryHeader == "History · 1 needs attention"`, and the Queue card is still there); `Newest_jobs_come_first`; `Yesterday_and_older_days_are_labelled` (finished yesterday → "Yesterday"; 2026-09-12 → "12 Sep 2026" with invariant month names); `Retry_failed_goes_to_the_queue_and_the_item_leaves_history` (command → Calls contains "RetryFailed 7"; then a Queued snapshot removes the item); `Failed_files_are_loaded_on_first_open` (toggle → "RequestFailedFiles 7" once; `RaiseFailedFiles` fills `FailedFiles`; second toggle hides without another request); `Open_folder_passes_the_destination`; `A_cancelled_job_is_listed_as_cancelled`.
  - `ViewModelTests` additions: `The_plan_starts_from_the_settings` (settings Replace + check off → `Overwrite == true`, and `Verify == true` because replacing forces it; settings Skip + check off → `Verify == false`); `QueueFinished_fires_when_the_last_job_ends` (Running then Done → fired once; a second Done snapshot doesn't fire again); `IsEmpty_reflects_cards_and_plans`.
  - Update the existing `NewMain` helper for the new constructor (in-memory settings store with defaults); existing assertions on `Subtitle == "Nothing to copy. Drop files or folders here."` change to `"Nothing is copying."` — the only allowed edits to existing tests.
- [ ] **Step 2: Run** → build FAILS.
- [ ] **Step 3: Implement.** Use `System.Text.Json` with `JsonStringEnumConverter`; tolerate unknown enum strings by catching `JsonException` → defaults.
- [ ] **Step 4: Run** `dotnet test` → all pass (16 new), no warnings.
- [ ] **Step 5: Commit** `feat: settings, history and a self-explaining queue in the view models`.

---

### Task 4: The window — tabs, empty state, history, settings, icon

**Files:** Modify `src/Squeue.App/MainWindow.xaml`, `MainWindow.xaml.cs`, `App.xaml.cs`, `Squeue.App.csproj`; add `src/Squeue.App/Assets/squeue.ico`, `squeue-512.png`, `squeue-32.png` (already on disk).

- [ ] **Step 1: csproj** — `<ApplicationIcon>Assets\squeue.ico</ApplicationIcon>`, and `<Resource Include="Assets\squeue.ico" />`; window `Icon="Assets/squeue.ico"`.
- [ ] **Step 2: App.xaml.cs** — settings path `%LOCALAPPDATA%\Squeue\settings.json`; create `JsonSettingsStore`; pass `HistoryDays` into `JobRunnerOptions`; pass the store and an `openFolder` action (`Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })`, only when the directory exists) to `MainViewModel`; subscribe `QueueFinished` → by `Settings.Current.WhenDone`: `SystemSounds.Asterisk.Play()`, or flash the taskbar button (`FlashWindowEx` P/Invoke on the main window handle, flash until the window comes to the foreground), or nothing.
- [ ] **Step 3: MainWindow.xaml** — window default size 520 × 720. Layout, following the approved mockup:
  - Header: "Squeue" (20 px, semi-bold) and to its right the secondary text "Copies files safely, one drive at a time".
  - `TabControl` with three tabs: "Queue", `{Binding HistoryHeader}`, "Settings".
  - **Queue tab:** label "Your drives · a filled dot means the drive is being used" above the drive pills. Then the plan panel (as today). Then, when `IsEmpty`: a dashed rounded drop area with "Drop files or folders here", "or choose them yourself", and two text buttons "Copy a folder…" (accent) and "Copy files…"; below it the label "How it works" and three equal boxes: "1 · Choose — What to copy and where to", "2 · Copy — One job per drive, others wait their turn", "3 · Check — Every copy is read back and compared". When not empty: the same two buttons in a compact right-aligned row, then the label "Jobs" and the card list. Job cards keep their layout but the icon buttons become text buttons: "Pause", "Resume", "Cancel", "Dismiss" (same visibility bindings; keep `AutomationProperties.Name`).
  - **History tab:** when `History` is empty, the text "Finished jobs appear here." Otherwise a scrolling list grouped by `DayLabel` (use a `CollectionViewSource` with a `PropertyGroupDescription` on `DayLabel`; group header = small secondary text). Each item is a card: title, route + details line, result at the right (`NeedsAttention` → semi-bold primary text; otherwise secondary), reason line (caution brush) when present, then buttons "Open folder", "Show failed files"/"Hide failed files" and "Retry failed" (the last two only when `CanRetry`). The failed-files list shows each file's name (tooltip = full path) and reason, max height 180 with its own scroll.
  - **Settings tab:** four rows, each a title, a one-line explanation in secondary text, and a control at the right: "Check every file after copying" / "Reads each copy back and compares it. Slower, safer." (toggle); "When a file already exists" / "You can still change this for each job." (combo); "When everything is done" / "What Squeue does when the queue is empty." (combo); "Keep history for" / "Older jobs are removed from the list at the next start." (combo). Combos bind `ItemsSource` to the choice lists with `DisplayMemberPath="Label"`, `SelectedValuePath="Value"`, `SelectedValue` two-way.
  - Remove the old footer "N done today" text (History replaces it); keep the `DoneToday` property if tests use it.
- [ ] **Step 4: Verify** — `dotnet build Squeue.slnx` 0 warnings (only if no Squeue.exe is running; never kill the user's), `dotnet test` all passing. Smoke-launch the built exe: alive after 6 s, window title "Squeue"; then, using UI Automation from PowerShell (`System.Windows.Automation`), assert the three tab items exist with names "Queue", a name starting with "History", and "Settings", and that buttons named "Copy a folder…" and "Copy files…" exist; stop the process. Report what was found. Also check the exe has the icon: `[System.Drawing.Icon]::ExtractAssociatedIcon(path)` is not null.
- [ ] **Step 5: Commit** `feat: tabs, history, settings, a clearer empty state and the app icon`.

---

### Task 5: One-command build

**Files:** Create `build.ps1`; modify `.gitignore` (add `dist/`), create `README.md`.

- [ ] **Step 1:** `build.ps1`: runs `dotnet test` (stop on failure), then `dotnet publish src/Squeue.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o dist/Squeue`, then prints the exe path and size. A `-SkipTests` switch skips the test run.
- [ ] **Step 2:** `README.md` (short): what Squeue is (two sentences), a screenshot placeholder line is NOT added; "Build" (`./build.ps1`), "Run from source" (`dotnet run --project src/Squeue.App`), where data lives (`%LOCALAPPDATA%\Squeue`), and a "Status" list of what's built and what's next.
- [ ] **Step 3: Run** `powershell -ExecutionPolicy Bypass -File build.ps1`; confirm `dist\Squeue\Squeue.exe` exists, report its size, smoke-launch it (alive 6 s, title "Squeue"), stop it.
- [ ] **Step 4: Commit** `build: one-command release build` (build.ps1, .gitignore, README.md only).
