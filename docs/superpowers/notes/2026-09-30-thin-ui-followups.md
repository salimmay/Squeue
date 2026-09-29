# Thin UI — rulings and deferred follow-ups

Carried from the execution ledger of the thin-UI plan (2026-09-30). Each deferred item names the later plan that owns it where known.

| T1 self | Step 1 creates branch feat/thin-ui vs branch already exists (controller created it to commit the plan) | Conflict -> Ruling R1 |
Ruling R1: Task 1 skips Step 1 (branch exists, plan committed at 81c0c92) — cost if wrong: none.
Ruling R2: Tasks 1 and 3 batched into one implementer dispatch and one review (independent, small, full code in plan; Task 2 stays separate because it changes the safety-critical copier) — cost if wrong: a finding in one delays the other.
Ruling R3: untrack tools/ via a follow-up commit and ignore it rather than rewriting local history — cost if wrong: demo files remain in two historical commits (harmless).
Ruling R4: fix IsSameOrInside with single-separator prefixes and unit-test it (internal) — cost if wrong: none.
Ruling R5: replace IgnoreInaccessible with a manual walk that records unreadable folders in JobPlan.SkippedFolders and silently skips System Volume Information / $RECYCLE.BIN; Task 5 will surface the count in the plan panel — cost if wrong: small extra code; a card root with an unreadable user folder now shows a warning instead of silently missing files.
Task 1: minor (deferred): SetJobState on unknown id is a no-op; Enum.Parse on unknown state throws for the whole list
Task 1: minor (deferred): legacy jobs migrate as Queued (only plan-1 demo DBs affected)
Task 3: minor (deferred): single-file source inside destination copies onto itself; duplicate dest paths from multiple sources not detected; empty folders not recreated; multi-source label is first source's parent; planner uses File/Directory directly, not IFileSystem
Task 3: minor (deferred): subfolder-listing failure marks the folder skipped though its files are planned; an IOException mid-file-loop drops rest of folder; unreadable top-level source yields zero files + skipped (UI should warn); no test populating SkippedFolders
Task 2: minor (deferred): entry cancelled during published verification stays Active (runner iterates all non-Done/Failed entries, so it is resumed — checked against Task 4 code)
Task 2: minor (deferred): no test for cancel during replace-path temp verification
Task 2: minor (deferred): zero-byte files emit no progress; replace path emits Verifying twice (runner counts Copying only)
Task 2: minor (deferred): OpenSourceWithRetry sleep, Preallocate, Flush ignore cancellation (latency only)
Task 2: minor (deferred): IOException from Abandon inside OCE catch escapes as IOException (Reconciler recovers); exceptions from IProgress.Report escape (UI adapter note)
Ruling R6: exceptions isolated per subscriber, per command, around startup reconcile, and a last-resort catch in Run (Trace) — cost if wrong: errors are only traced, not shown in UI yet.
Ruling R7: an exception from CopyEntry pauses the job (reconcile, keep entry state, remember reason in LastError) instead of failing the file — drive/journal trouble isn't a bad file; resume retries — cost if wrong: a truly broken single file pauses the whole job until the user cancels it.
Ruling R8: _disposed flag under _gate cancels any job that starts after Dispose; loop also checks IsAddingCompleted — cost if wrong: none.
Ruling R9: idempotent Dispose; commands after Dispose silently dropped; Start after Dispose throws ObjectDisposedException; _commands no longer disposed — cost if wrong: a click during shutdown is ignored.
Ruling R10: publish after each file with force: false — cost if wrong: file-count text can lag up to 250 ms.
Task 4: minor (deferred): Dispatcher.Invoke in a JobChanged handler would deadlock Dispose — App uses BeginInvoke (Task 6)
Task 4: minor (deferred): Pause/Cancel race can briefly requeue and restart the current file (wasted I/O, flicker)
Task 4: minor (deferred): commands queued right before Dispose are dropped (an Enqueue at exit is lost)
Task 4: minor (deferred): cancelling during verification of a published copy leaves an unverified file with an open attempt in a Cancelled job
Task 4: minor (deferred): speed counts resumed (already-copied) files and whole-run average
Task 4: minor (deferred): tests don't cover cancel of Queued/Paused, resume of Done, Paused surviving restart, two jobs in order, throttling, cancel during verify
Task 4: minor (deferred): stop-reason catch skips all OperationCanceledException, not only our token's (foreign OCE ends runner thread quietly)
Task 4: minor (deferred): microsecond requeue loop if RunJob starts between _disposed and CompleteAdding
Task 4: minor (deferred): drive error during Dispose leaves job Paused (needs manual Resume after restart)
Task 4: minor (deferred): broken journal ends runner thread with no UI signal; Dispose from a subscriber self-joins; LastError kept after Cancel
Ruling R11: Format.TimeLeftFor(bytesLeft, speed) with "" for nothing/no speed and "more than a day left" past 24 h, used by card and subtitle; Percent floors unless Done — cost if wrong: none.
Ruling R12: add notification, post-marshalling and empty-job tests — cost if wrong: a few extra tests.
Task 5: minor (deferred): Bytes unit boundaries ("1024 KB"); TimeLeft ceiling at 3599.5 s -> "60 min left"; _latest grows for the session; DoneToday keyed on CreatedAt, not refreshed at midnight; Percent/Done-failed text uses current culture; singular skipped note and DismissCommand untested
Ruling R13: catch Exception at the planning UI boundary plus an App DispatcherUnhandledException last resort — cost if wrong: an unexpected bug shows a message instead of crashing (could hide bugs; message text still shown).
Ruling R14: single-instance Mutex per user with an "already running" message; surfacing a runner that died for other reasons deferred to plan 5 — cost if wrong: a corrupt journal still leaves a dead UI until plan 5.
Ruling R15: AutomationProperties.Name on all icon buttons — cost if wrong: none.
Task 6: minor (deferred): second drop replaces a pending plan; Cancel on a card has no confirmation; OnExit blocks on Join; VolumeLabel can throw if a drive vanishes, drives read once; second WindowsFileSystem instance; colour-only busy dot; skipped note between checkboxes; Appear ignores reduced motion
Task 6: minor (deferred): a Handled exception during OnStartup could leave the app running with no window
Ruling R16: C1 — keep Done-with-failures and Cancelled cards until dismissed, show last error, DoneToday counts clean jobs only. Cost if wrong: extra cards to dismiss.
Ruling R17: C2 — CopyResult carries the Win32 error; runner pauses (entry back to Pending) on device/disk-full codes 21/55/433/1167/112/39 or a missing source/destination volume, and never starts a queued job whose volume is missing. Cost if wrong: a genuinely bad single file with one of those codes pauses the job instead of failing.
Ruling R18: I1 — Reconciler defers (leaves untouched) attempts whose destination volume is missing or whose inspection throws IOException. Cost if wrong: temp files on an absent drive wait until it returns.
Ruling R19: I2 — drain commands from the progress callback (never inside Atomically). Cost if wrong: none.
Ruling R20: I3 — Skip only skips same-size same-time files; different same-name files are reported as failed entries. Cost if wrong: time-only differences (e.g. FAT 2 s precision) show as "differ" — acceptable for now.
Ruling R21: I4 — add entries(job_id) index; startup publishes unfinished jobs plus today's finished ones. Cost if wrong: older failed jobs are not shown after a day.
Ruling R22: I5 — per spec, reparse/online-only files are skipped and counted in the plan panel (not hydrated). Cost if wrong: OneDrive users must download files first.
Ruling R23: I6 — 5 s shutdown join; startup wrapped; drive lookups tolerate vanishing drives. Cost if wrong: a stop longer than 5 s ends mid-write, which plan 1 recovers.
Ruling R24: also fix now: Replace forces Verify; refuse a folder whose target equals the source; drive pills refresh on WM_DEVICECHANGE; add tests for two jobs in order, paused-survives-restart, cancel during replacement check. Cost if wrong: none.
Final: residual (surfaced to user): after relaunch without the backup drive, the first Resume once it's reconnected can pause again with "unreconciled attempt" (second Resume works; no data risk) — reconcile before running a resumed job
Final: residual (surfaced to user): core still accepts Replace with verify off (UI forbids it) — enforce in JobRunner.AddJob
Final: minor (deferred): MissingVolume checks only job Source/DestRoot, not each entry's roots (multi-drive drops)
Final: minor (deferred): Paused card Detail may show an earlier file's error; dismissals not persisted across same-day relaunch
Final: minor (deferred): I3 exact time match reports identical files on FAT/exFAT backups as "different" (not replaced) — add 2 s tolerance
Final: minor (deferred): startup Reconciler per-attempt catch only IOException; WM_DEVICECHANGE refresh runs IsReady on UI thread
