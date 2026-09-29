# Core copy protocol — rulings and deferred follow-ups

Carried from the execution ledger of plan 1 (2026-09-29). Each deferred item names the later plan that owns it.

| T1 self | git init + checkout -b + `dotnet new gitignore` vs repo already initialised by controller with .gitignore (.superpowers/) | Conflict -> Ruling R1 |
| T1 self | WindowsFileSystem stubs throw NotImplementedException for CreateTemp/OpenForVerify/DeleteIfSameObject | Plan-mandated staging -> Ruling R2 |
Ruling R1: Task 1 skips `git init` and `git checkout -b` (repo and branch exist); runs `dotnet new gitignore --force` then re-appends `.superpowers/` — keeps the SDD workspace ignored — cost if wrong: workspace files could be committed (visible in review packages).
Ruling R2: NotImplementedException stubs in Task 1 are accepted as plan-mandated staging; Tasks 2-3 remove them — cost if wrong: none after Task 3; reviewer may flag.
Ruling R3: accept Squeue.slnx instead of Squeue.sln — .NET 10 `dotnet new sln` default and nothing in the plan references the file by name — cost if wrong: rename one file.
Ruling R4: every implementer commit must carry the Opus 5.5 trailer from the Global Constraints; state it explicitly in each dispatch — cost if wrong: none.
Task 1: minor (deferred): CreateDirectory uses Directory.CreateDirectory (no \?\ / FsException) — .NET handles long paths; long-path coverage arrives in Task 8
Task 1: minor (deferred): TryGetIdentity maps only not-found/path-not-found to null; doc comment could say so
Task 1: minor (deferred): FsException message shows only numeric Win32 code; HResult not set
Task 1: minor (deferred): ReadIdentity silently falls back from FileIdInfo on any failure (including access denied)
Task 1: minor (deferred): Native.LongPath mishandles \.\ device paths
Task 1: minor (deferred): test polish — FileStream leak on assertion failure, Sleep(50) timing, no FileId bytes round-trip test, no IsSameVersion negative for size/time
Ruling R5: Task 1 round-1 re-review done as a mechanical check (tree-identical amend, only the message changed) instead of a reviewer dispatch — cost if wrong: none, no code changed.
Task 2: minor (deferred): FAT/exFAT legacy disposition fallback can't delete read-only files (plan-mandated) — relevant for plan 4 (FAT support)
Task 2: minor (deferred): no test pins temp share mode (read-only share)
Task 2: minor (deferred): replace onto read-only destination fails with access denied (untested)
Task 3: minor (deferred): AlignedBuffer has no finalizer; Span may outlive buffer (document)
Task 3: minor (deferred): IVerifyFile.Read doesn't validate alignment
Task 3: minor (deferred): no test proves NO_BUFFERING is set; no missing-path / dispose tests
Ruling R6: Tasks 4 and 5 batched into one implementer dispatch and one review (both small, independent, new-file-only, full code in plan) — cost if wrong: a finding in one could delay the other.
Task 4: minor (deferred): IncrementalHash never disposed (IContentHasher not IDisposable) — plan-mandated
Task 4: minor (deferred): FinishHex reset semantics differ between crypto and non-crypto hashers
Task 5: minor (deferred): HasTempShape regex `$` accepts trailing newline; use \z
Ruling R7: add a second-connection-without-dispose test (keeps the plan's test) — cheap and pins the crash-restart path — cost if wrong: one extra test.
Ruling R8: document Journal as single-threaded rather than add locking — plan 5 gives the journal one writer thread — cost if wrong: a later concurrent caller could corrupt ids; the doc line warns.
Task 6: minor (deferred): Open leaks connection if pragma/schema throws
Task 6: minor (deferred): Open doesn't verify journal_mode actually became WAL (e.g. network share)
Task 6: minor (deferred): IdentityBlob.Decode doesn't check length
Task 6: minor (deferred): test gaps — COALESCE preservation after Finished, AttemptsFor order, missing id, >64-bit FileId
Task 6: minor (deferred): public Pragma(string) interpolates SQL
Task 6: minor (deferred): SetEntryState clears error when omitted
Task 7: minor (deferred): Source.Read, CreateDirectory, GetIdentity not instrumented — crash mid-source-read is equivalent to crash before the next Write, which the matrix covers
Task 7: minor (deferred): occurrence counts cumulative per instance, never reset
Task 7: minor (deferred): thin FaultyFileSystem tests (after-crash on non-open ops, disposal of OpenSource/OpenForVerify)
Ruling R9: Task 8 implementer on sonnet (not haiku) — largest, multi-file task with the most interop and test-debugging risk — cost if wrong: somewhat higher spend.
Ruling R10: fix stale hashes by clearing both hashes when each attempt begins (new Journal.ClearHashes) — plan code was wrong; spec I2 requires verifying every published file — cost if wrong: one extra UPDATE per file.
Ruling R11: on post-publish mismatch, adopt the current destination as replace target only if its FileId equals the attempt's PublishedFileId; otherwise Fail and keep both files (spec I6) — cost if wrong: an entry fails that could have retried.
Ruling R12: add resume-from-Published and refuse-while-unreconciled tests — cost if wrong: two extra tests.
Task 8: minor (deferred): temp GetIdentity + TempCreated journal write outside try; failure leaves an un-journaled temp (reported as Unowned by reconciler)
Task 8: minor (deferred): Abandon inside try failing its delete → catch Abandons again and outcome becomes Fail
Task 8: minor (deferred): check-to-rename race window on Replace; IsSameVersion timestamp granularity — document
Task 8: minor (deferred): ReadOnly attribute copied to dest; later Replace/retry over it fails with access denied (FileRenameInfo lacks ignore-readonly) — plan 4
Task 8: minor (deferred): after two verify failures the unverified first copy stays at the final name; message doesn't say so
Task 8: minor (deferred): unknown hash algorithm throws after temp exists; validate before BeginAttempt
Task 8: minor (deferred): sharing retry loop untested; deadline uses DateTime.UtcNow (use Stopwatch)
Task 8: minor (deferred): post-publish verification IOException escapes CopyEntry as an exception (safe, resumable)
Task 8: minor (deferred): HashUnbuffered treats any short read as EOF (fails safe)
Task 8: minor (deferred): foreign-destination test relies on delete+write yielding a new FileId (NTFS file reference includes sequence number, so safe on NTFS)
Ruling R13: add IFileSystem.FlushIfSameObject and call it before marking a recovered rename Published; read-only destinations (access denied) return false and proceed unflushed — cost if wrong: for read-only files only, a power cut in the seconds after recovery could undo a rename already marked Done; fully handled in plan 4 (apply ReadOnly after durability).
Ruling R14: write entry state before closing the attempt in Reset/Failed branches — cost if wrong: none.
Task 9: minor (deferred): creation-time heuristic trusts destination clock (SMB skew); comment only
Task 9: minor (deferred): idempotence test covers only Reset path; untested combos (temp gone + foreign dest; Published + missing dest; VerifiedTemp resume with DestHash)
Task 9: minor (deferred): Failed message doesn't distinguish missing vs foreign destination
Ruling R15: add a second theory that forces verification failures and asserts the crash fired at TryGetIdentity/Delete; authorize FileCopier fix to record the verify-failure outcome before closing the attempt (suspected stranding) — cost if wrong: a few extra tests / one reorder.
Task 10: minor (deferred): I2 check can't detect early publication of a complete-but-unflushed file
Task 10: minor (deferred): I3 checks only Reset/Failed empty on second run, not unchanged disk/journal
Task 10: minor (deferred): RetryNeeded retry could mask a defect
Task 10: minor (deferred): no crash during Reconciler.Run itself (double crash); Source.Read/FlushIfSameObject/CreateDirectory/GetIdentity unhooked; journal commit failures not expressible (plan 5)
Task 10: minor (deferred): journal-only window between SetEntryState(Pending) and SetPhase(Abandoned) in FinishPublished ends Failed (safe, source kept) because SrcHash was cleared; FinishPublished treats null SrcHash as mismatch — follow-up
Ruling R16: fix (1) with Journal.Atomically transactions at every terminal write pair. Cost if wrong: small refactor risk, covered by tests.
Ruling R17: fix (2) now (ownership check at top of FinishPublished) rather than in plan 2. Cost if wrong: one extra identity read per file.
Ruling R18: fix (3) as a message change only; renaming or deleting the damaged copy deferred to plan 7 UX. Cost if wrong: user deletes the bad copy by hand.
Ruling R19: defer (4) per-attempt error isolation in Reconciler to plan 5 (recovery-mode owner). Cost if wrong: one unreadable file blocks recovery of the others until plan 5.
Ruling R20: apply cheap items now: regex \z, Pragma internal, validate hash before attempt, PublishedFileId read after rename, stronger I3 asserts. Cost if wrong: none.
Final: minor (deferred): AttemptPhase.Verified no longer written (legacy only)
Final: minor (deferred): Reconciler recovered-rename journals pre-rename id; on FAT/exFAT the F2 check then fails safe (plan 4)
Final: minor (deferred): BeginAttempt/ClearHashes/SetEntryState(Active) separate commits (reconciler resets; safe)
