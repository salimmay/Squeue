# App shell — rulings and deferred follow-ups

Carried from the execution ledger of the app-shell plan (2026-10-01).

| T1 self | Prune test wording about finished_at is muddled | Ruling R2 |
Ruling R1: this plan gives interfaces, behaviours, texts and test lists but not full code for every file (the implementers read the code) — keeps the plan reviewable; cost if wrong: more implementer judgment, caught by reviews.
Ruling R2: in Task 1's prune test, a future cutoff (now + 1 day) is the intended way to make finished jobs "old"; ignore the muddled parenthesis — cost if wrong: none.
Task 1: minor (deferred): prune boundary (job newer than cutoff kept) untested; FinishJob doesn't check state; negative limit unlimited
Ruling R3: accept the rename/re-assertion of Startup_shows_unfinished_jobs_and_todays_finished_ones_only — the plan replaced that rule and failed to list the test — cost if wrong: none.
Ruling R4: RetryFailed acts only on Done jobs (not Cancelled — it would undo the cancel) — cost if wrong: a cancelled job's failed files can't be retried without re-adding the job.
Task 2: minor (deferred): cancelled jobs show 0 % (summary has no done-bytes) — Task 4 hides the bar and percent on cancelled cards
Task 2: minor (deferred): cancel-time summary can be one file stale after an ordinary quit; legacy jobs backfilled with finish time = first launch; retry with the conflict still present gives a misleading reason; test gaps (HistoryDays 0, legacy Cancelled backfill)
Ruling R5: JobCardViewModel gets ShowProgress (false for Cancelled) so a cancelled card doesn't show a 0 % bar; RetryFailed offered only for Done jobs with failures — cost if wrong: none.
Task 3: minor (deferred): CanRetry-for-cancelled test is tautological; no test that startup finished jobs don't fire QueueFinished; settings tests thin (no save-on-construct pin, two hooks untested); invariant-month test not culture-pinned; DayLabel stale after midnight; QueueFinished raised before Subtitle/busy drives refresh; plural "need attention" untested; Load doesn't range-check values
Task 4: minor (deferred): job card says "Verifying" (UI rule says "check"); while a plan is pending with no jobs the "Jobs" label and button row show above an empty list; combo blank for a hand-edited HistoryDays; DayLabel stale over midnight
Investigation result: could not reproduce the lost jobs. Probe: journal data survives clean close and kill; the real app (current build) started with a marker job, ran it to Done, closed gracefully, checkpointed the db (4096 -> 24576 bytes) and kept the job. Marker removed afterwards. Earlier loss unexplained (before today the app's db had never been checkpointed — every session ended by kill or reboot).
Final: follow-up: unexplained loss of earlier job rows from the WAL-only database — add a startup log line with job counts and a periodic wal_checkpoint so data doesn't live only in the WAL for days
Ruling R6: Task 5 (script + README) is reviewed in the final whole-branch review rather than separately — cost if wrong: a script issue is caught one step later.
Ruling R7: a finished job gets a Queue card only if this session saw it active; history shows the rest — cost if wrong: a failure from a previous session is visible only in History (with the "needs attention" header).
Ruling R8: Replace default applies only when the plan has existing files — cost if wrong: none.
Ruling R9: select the Queue tab after proposing a plan or retrying — cost if wrong: an unexpected tab switch.
Ruling R10: add Journal.Checkpoint (passive) at startup and after each job, and a startup job-count trace, in response to the unexplained earlier loss of job rows — cost if wrong: a little extra I/O per job.
Ruling R11: deferred to later plans — history list virtualisation, "needs attention" acknowledge, done-signal on cancel, cancel-time summary staleness, legacy finish time, DayLabel over midnight, test gaps listed by the final review.
Final: minor (deferred): Checkpoint counts as a commit in CommitCount; build.ps1 lock probe catches only IOException
