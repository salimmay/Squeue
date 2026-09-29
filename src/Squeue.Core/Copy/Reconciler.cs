using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Core.Copy;

public sealed class ReconcileReport
{
    /// Entries whose unfinished attempt was cleaned up; they are pending again.
    public List<long> Reset { get; } = [];

    /// Entries whose file is published; FileCopier.CopyEntry finishes them.
    public List<long> Resumed { get; } = [];

    public List<long> Failed { get; } = [];

    /// Files at a journaled temp name that we couldn't prove are ours. Left untouched.
    public List<string> Unowned { get; } = [];
}

/// After a crash, compares every open journal attempt with what is actually on disk and brings it
/// to a state FileCopier can resume from. Safe to run any number of times.
public sealed class Reconciler(IFileSystem fs, Journal journal)
{
    // Clock slack when deciding whether an un-journaled temp file was created by this attempt (FILETIME units).
    private const long CreationSlack = 2 * 10_000_000;

    public ReconcileReport Run()
    {
        var report = new ReconcileReport();
        foreach (var attempt in journal.OpenAttempts())
            Reconcile(journal.GetEntry(attempt.EntryId), attempt, report);
        return report;
    }

    private void Reconcile(Entry entry, Attempt attempt, ReconcileReport report)
    {
        string tempPath = Path.Combine(entry.DestDirectory, attempt.TempName);

        switch (attempt.Phase)
        {
            case AttemptPhase.Published or AttemptPhase.Verified:
                if (fs.TryGetIdentity(entry.DestPath) is { } published && published.FileId == attempt.PublishedFileId)
                {
                    report.Resumed.Add(entry.Id);
                    return;
                }
                // The entry's new state and the attempt's close commit together.
                journal.Atomically(() =>
                {
                    journal.SetEntryState(entry.Id, EntryState.Failed, "The destination changed after it was copied. The source was kept.");
                    journal.SetPhase(attempt.Id, AttemptPhase.Abandoned);
                });
                report.Failed.Add(entry.Id);
                return;

            default: // Intent, TempCreated, Written, VerifiedTemp
                // The rename only ever happens after Written is committed, so only those phases can have published.
                bool mayHavePublished = attempt.Phase is AttemptPhase.Written or AttemptPhase.VerifiedTemp;
                if (mayHavePublished
                    && attempt.TempFileId is { } tempId
                    && fs.TryGetIdentity(tempPath) is null
                    && fs.TryGetIdentity(entry.DestPath) is { } final
                    && final.FileId == tempId)
                {
                    fs.FlushIfSameObject(entry.DestPath, tempId); // FileCopier flushes after its rename; this recovered rename gets the same
                    journal.SetPhase(attempt.Id, AttemptPhase.Published, publishedFileId: tempId);
                    report.Resumed.Add(entry.Id);
                    return;
                }

                RemoveTempIfOurs(attempt, tempPath, report);
                // The entry's new state and the attempt's close commit together.
                journal.Atomically(() =>
                {
                    journal.SetEntryState(entry.Id, EntryState.Pending);
                    journal.SetPhase(attempt.Id, AttemptPhase.Abandoned);
                });
                report.Reset.Add(entry.Id);
                return;
        }
    }

    private void RemoveTempIfOurs(Attempt attempt, string tempPath, ReconcileReport report)
    {
        if (fs.TryGetIdentity(tempPath) is not { } found) return;

        bool ours = attempt.TempFileId is { } id
            ? found.FileId == id
            : found.CreationTime >= attempt.StartedAt - CreationSlack; // crashed before the id was journaled

        if (ours) fs.DeleteIfSameObject(tempPath, found.FileId);
        else report.Unowned.Add(tempPath);
    }
}
