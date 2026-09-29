# Several drives at once — rulings and deferred follow-ups

Carried from the execution ledger of the multi-drive plan (2026-09-29).

Ruling R1: Tasks 3 and 4 are batched into one implementer dispatch and one review (both new-file-only, independent, full code in plan) — cost if wrong: a finding in one delays the other.
Ruling R2: add a deterministic held-transaction test and an experiment removing busy_timeout/IMMEDIATE; move the wait to DefaultTimeout on the connection string if effective — cost if wrong: none.
Task 1: minor (deferred): JobRunner startup reset must cover Verifying (Task 5 code already does)
Ruling R3: re-review of the fix round done by the controller (source change is one connection-string line + comment; tests reviewed in report) — cost if wrong: a test-quality issue caught at final review.
Task 2: minor (deferred): replacement/no-hash deferred tests don't assert DestHash/content
Task 2: minor (deferred): resuming with defer returns Published without an ownership check (harmless; verification checks)
Ruling R4: accept the implementer's correction of Need_adds_up_rounded_sizes to 16384 — the plan's 12288 was an arithmetic error (4096+4096+8192) — cost if wrong: none.
Ruling R5: existing mount without a volume GUID -> unk:<mount>; \?\UNC\ -> net:SERVER; \?\X: and \.\X: local — cost if wrong: a mapped drive shares an unk: key per letter, not per server (two letters to one NAS may run in parallel).
Ruling R6: headroom = min(max(1 GB, 1 %), 10 % of the volume) — deviates from the spec's max(1 GB, 1 %) so small SD cards/sticks stay usable as destinations — cost if wrong: less spare space kept on volumes under 10 GB.
Ruling R7: Problem returns null when need is 0; Scheduler doc states a job with no disks starts at once — cost if wrong: none.
Tasks 3+4: minor (deferred): GetFullPath throws on invalid paths; >170 extents degrades to unk:; Text rounding ("1024 MB", independent rounding); tests assume temp on a lettered volume; AheadInQueue determinism and no-space-not-waiting untested
Tasks 3+4: minor (deferred): mapped/subst fallback untested (can't reproduce here)
Ruling R8: Task 5 implementer on opus — largest, concurrent rewrite with the most integration risk — cost if wrong: higher spend.
Task 5: implementer a3405ee04cb9a0fc1 (opus) DONE_WITH_CONCERNS 9bad25a; 296 passing, threaded tests 26x clean. Deviations: verify published files before a device-error pause; Pause/Cancel queue state before stopping, stop only the current run.
Task 5: review (af0e0cda47e67ba77, opus): spec ✅ (both deviations judged correct and needed), Approved.
Task 5: minor (deferred, flag for final): strict per-disk order gap — a Queued job whose previous worker is still stopping has its source disk unreserved; reserve its next-phase disks in the pass
Task 5: minor (deferred, flag for final): during verification a Failed result checks MissingVolume(job) incl. the freed source -> misleading "card isn't connected" pause; only the destination should count
Task 5: minor (deferred): verify-first pass can replace the friendly device reason with a raw message; full-destination reason shown late
Task 5: minor (deferred): every pass re-reads all queued jobs' entries and resolves disks per folder (cache later)
Task 5: minor (deferred): capacity reservation can't fire with real topology; doesn't count same-pass grants
Task 5: minor (deferred): no tests for resume-while-stopping, verify-only restart, Dispose with several workers
Task 5: minor (deferred): same-process restart can overlap a lingering worker with the startup Reconciler (tests only)
Task 6: minor (deferred): subtitle "left" undercounts (excludes later verify pass); progress bar restarts at 0% when verification starts; tests missing for WaitingFor fallback / NeedsCard Verifying
Ruling R9: during verification only the destination drive counts as missing; non-device per-file failures stay Failed and verification continues — cost if wrong: none (source isn't needed to verify).
Ruling R10: add an internal RootExists seam in JobRunnerOptions so tests can simulate a pulled card — cost if wrong: a test-only hook in production options.
Ruling R11: strict-order gap during a resume-while-stopping, cancel leaving unverified copies, pass cost, capacity-reservation dead code — deferred to later plans per the final review's triage — cost if wrong: a rare overtaking on a card; cancelled jobs' copies remain unverified until plan 7.
Final: minor (deferred): F4 test's old worker can outlive the 300 ms shutdown and publish stray events after the gate opens (passed 15x; low risk)
Final: minor (deferred): a job whose pause itself fails stays Queued and is retried every pass (logged)
Final: minor (deferred): cancelling leaves every copied-but-unverified file at its final name with an open attempt — plan 7 (cancel wording / close attempts)
Final: minor (deferred): strict-order gap during resume-while-stopping; per-pass cost; capacity reservation dead in v1 — later
