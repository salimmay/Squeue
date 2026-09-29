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
