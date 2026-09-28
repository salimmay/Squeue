using Squeue.Core.FileSystem;

namespace Squeue.Tests.FileSystem;

public class IdentityTests
{
    private readonly WindowsFileSystem _fs = new();

    [Fact]
    public void Identity_is_stable_for_one_file_and_differs_between_files()
    {
        using var dir = new TestDir();
        string a = dir.Write("a.bin", [1, 2, 3]);
        string b = dir.Write("b.bin", [1, 2, 3]);

        var a1 = _fs.TryGetIdentity(a)!.Value;
        var a2 = _fs.TryGetIdentity(a)!.Value;
        var b1 = _fs.TryGetIdentity(b)!.Value;

        Assert.True(a1.IsSameVersion(a2));
        Assert.False(a1.IsSameObject(b1));
        Assert.Equal(3, a1.Size);
    }

    [Fact]
    public void Rewriting_a_file_changes_its_version_even_if_the_write_time_is_restored()
    {
        using var dir = new TestDir();
        string path = dir.Write("a.bin", [1, 2, 3]);
        var before = _fs.TryGetIdentity(path)!.Value;
        var originalWriteTime = File.GetLastWriteTimeUtc(path);

        Thread.Sleep(50);
        File.WriteAllBytes(path, [9, 9, 9]);
        File.SetLastWriteTimeUtc(path, originalWriteTime);
        var after = _fs.TryGetIdentity(path)!.Value;

        Assert.True(before.IsSameObject(after));
        Assert.Equal(before.LastWriteTime, after.LastWriteTime);
        Assert.False(before.IsSameVersion(after)); // ChangeTime moved
    }

    [Fact]
    public void TryGetIdentity_returns_null_when_nothing_is_there()
    {
        using var dir = new TestDir();
        Assert.Null(_fs.TryGetIdentity(dir.PathOf("missing.bin")));
        Assert.Null(_fs.TryGetIdentity(dir.PathOf(@"no\such\folder\missing.bin")));
    }

    [Fact]
    public void OpenSource_reads_the_content_and_reports_its_identity()
    {
        using var dir = new TestDir();
        var content = TestDir.RandomBytes(100_000);
        string path = dir.Write("a.bin", content);

        using var source = _fs.OpenSource(path);
        var read = new byte[content.Length];
        long offset = 0;
        int n;
        while ((n = source.Read(read.AsSpan((int)offset), offset)) > 0) offset += n;

        Assert.Equal(content, read);
        Assert.Equal(content.Length, source.Identity.Size);
    }

    [Fact]
    public void OpenSource_blocks_other_writers_while_open()
    {
        using var dir = new TestDir();
        string path = dir.Write("a.bin", [1]);

        using var source = _fs.OpenSource(path);

        Assert.ThrowsAny<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
    }

    [Fact]
    public void OpenSource_reports_a_sharing_violation_when_another_program_is_writing()
    {
        using var dir = new TestDir();
        string path = dir.Write("a.bin", [1]);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        var ex = Assert.Throws<FsException>(() => _fs.OpenSource(path));

        Assert.True(ex.IsSharingViolation);
    }
}
