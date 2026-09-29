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

    [Theory]
    [InlineData(@"\\nas\photos\2026")]
    [InlineData(@"\\?\UNC\nas\photos")]
    public void A_network_share_is_one_disk_per_server(string path) =>
        Assert.Equal(new[] { "net:NAS" }, new WindowsDiskTopology().DisksOf(path));

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
