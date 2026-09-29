# Speed — rulings and deferred follow-ups

Carried from the execution ledger of the speed plan (2026-09-29).

Ruling R1: add a "Windows + flush each file" engine to the bench so the comparison is like-for-like with Squeue's durability rule; Task 5 also measures the big file and the small files separately — cost if wrong: one extra engine row.
Ruling R2: flush-parity engine + --rounds 3 medians with rotated order + make refuses non-empty folders; rerun baseline — cost if wrong: longer bench runs.
Task 1: minor (deferred): Option parsing unvalidated; cleanup catches only IOException; depth printed but inert until Task 4
Task 2: minor (deferred): entry failing at CreateDirectory has no src_version (nothing reads it yet; plan 2 moves must not assume it)
Task 3: minor (deferred): inline path returns on a late cancel where overlapped throws (carry to Task 4: FileCopier behaviour must not depend on it — both end in a cancellation check before publishing? note)
Task 3: minor (deferred): a reader error during a simultaneous user cancel is dropped (cancel wins)
Task 3: minor (deferred): onRead runs concurrently with consume — keep shared state out (Task 4 carries it)
Task 3: minor (deferred): "reader stops" test bound can't fail; missing tests for inline stopOnShortRead/cancel, pre-cancelled token
Ruling R3: in Task 4, add a cancellation check at the end of RunInline so both paths end the same way on a late cancel (one line + pre-cancelled-token test) — cost if wrong: none.
Task 4: minor (deferred): simultaneous read error + cancel reports Failed rather than Cancelled; read-error test doesn't assert ReaderThreadsStarted; ring (16 MiB) allocated even when only small files
Ruling R4: Task 5 (docs only, numbers from a tool reviewed in Task 1) is reviewed as part of the final whole-branch review instead of a separate task review — cost if wrong: a wording issue in the note is caught one step later.
Ruling R5: the fix-wave re-review of the 8-line note change was done by the controller reading the diff (all three overclaims replaced, numbers untouched) instead of a reviewer dispatch — cost if wrong: a wording issue survives in a note.
Ruling R6: bench keeps adding journal entries inside the timed loop (conservative for Squeue); stated in the note rather than re-measuring — cost if wrong: small-file numbers slightly pessimistic.
Final: minor (deferred): buffer ring (16 MiB pinned) allocated on first copy and kept for the app's lifetime; allocate lazily — plan 5 (per-worker pipelines)
Final: minor (deferred): after R3 a small file that finished writing then saw a late cancel is abandoned and recopied (safe)
Final: follow-up: ~4.9 ms per small file when verifying — likely antivirus scanning newly written files on first open; measure with a Defender exclusion or a trace
Final: follow-up: measure read-ahead on a cold, slow source (SD card / USB) — the warm-cache bench can't show it
