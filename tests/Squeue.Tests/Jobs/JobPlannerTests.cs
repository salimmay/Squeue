using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;

namespace Squeue.Tests.Jobs;

public class JobPlannerTests
{
    private readonly WindowsFileSystem _fs = new();

    [Fact]
    public void A_single_file_goes_straight_into_the_destination()
    {
        using var dir = new TestDir();
        string src = dir.Write(@"card\IMG_0001.CR3", new byte[100]);

        var plan = JobPlanner.Plan(_fs, [src], dir.PathOf("backup"));

        var file = Assert.Single(plan.Files);
        Assert.Equal(dir.PathOf(@"backup\IMG_0001.CR3"), file.DestPath);
        Assert.Equal(100, file.Size);
        Assert.Equal(("IMG_0001.CR3", src, dir.PathOf("backup")), (plan.Name, plan.Source, plan.DestRoot));
    }

    [Fact]
    public void A_folder_keeps_its_name_and_structure()
    {
        using var dir = new TestDir();
        dir.Write(@"card\DCIM\a.jpg", new byte[10]);
        dir.Write(@"card\DCIM\100CANON\b.cr3", new byte[20]);

        var plan = JobPlanner.Plan(_fs, [dir.PathOf(@"card\DCIM")], dir.PathOf("backup"));

        Assert.Equal("DCIM", plan.Name);
        Assert.Equal(
            new[] { dir.PathOf(@"backup\DCIM\100CANON\b.cr3"), dir.PathOf(@"backup\DCIM\a.jpg") },
            plan.Files.Select(f => f.DestPath).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(30, plan.TotalBytes);
    }

    [Fact]
    public void Several_sources_are_named_by_count()
    {
        using var dir = new TestDir();
        string a = dir.Write(@"card\a.jpg", new byte[1]);
        string b = dir.Write(@"card\b.jpg", new byte[1]);

        var plan = JobPlanner.Plan(_fs, [a, b], dir.PathOf("backup"));

        Assert.Equal(("2 items", dir.PathOf("card")), (plan.Name, plan.Source));
        Assert.Equal(2, plan.Files.Count);
    }

    [Fact]
    public void Existing_destination_files_are_detected()
    {
        using var dir = new TestDir();
        string src = dir.Write(@"card\a.jpg", new byte[1]);
        string existing = dir.Write(@"backup\a.jpg", new byte[5]);

        var plan = JobPlanner.Plan(_fs, [src], dir.PathOf("backup"));

        Assert.Equal(1, plan.ExistingCount);
        Assert.Equal(_fs.TryGetIdentity(existing), plan.Files[0].ExistingDest);
    }

    [Fact]
    public void Copying_a_folder_into_itself_is_refused()
    {
        using var dir = new TestDir();
        dir.Write(@"card\a.jpg", new byte[1]);

        var ex = Assert.Throws<ArgumentException>(() => JobPlanner.Plan(_fs, [dir.PathOf("card")], dir.PathOf(@"card\copy")));

        Assert.Contains("into itself", ex.Message);
    }

    [Fact]
    public void A_missing_source_is_refused()
    {
        using var dir = new TestDir();
        Assert.Throws<FileNotFoundException>(() => JobPlanner.Plan(_fs, [dir.PathOf("nope")], dir.PathOf("backup")));
    }

    [Theory]
    [InlineData(@"F:\Backup", @"F:\", true)]
    [InlineData(@"F:\", @"F:\", true)]
    [InlineData(@"E:\card\copy", @"E:\card", true)]
    [InlineData(@"E:\CARD\copy", @"e:\card", true)]
    [InlineData(@"E:\cards", @"E:\card", false)]
    [InlineData(@"E:\backup", @"F:\", false)]
    public void IsSameOrInside_handles_roots_prefixes_and_case(string path, string folder, bool expected) =>
        Assert.Equal(expected, JobPlanner.IsSameOrInside(path, folder));

    [Fact]
    public void Windows_system_folders_are_left_out()
    {
        using var dir = new TestDir();
        dir.Write(@"card\System Volume Information\x.dat", new byte[1]);
        string a = dir.Write(@"card\a.jpg", new byte[1]);

        var plan = JobPlanner.Plan(_fs, [dir.PathOf("card")], dir.PathOf("backup"));

        Assert.Single(plan.Files);
        Assert.Equal(a, plan.Files[0].SourcePath);
        Assert.Empty(plan.SkippedFolders);
    }
}
