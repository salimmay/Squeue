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
        Assert.Equal(16384, Capacity.Need([1, 4096, 4097], 4096));

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
