using Squeue.Core.FileSystem;

namespace Squeue.Tests.FileSystem;

public class TempFileTests
{
    private static readonly long SomeTime = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToFileTimeUtc();
    private readonly WindowsFileSystem _fs = new();

    [Fact]
    public void CreateTemp_refuses_an_existing_name()
    {
        using var dir = new TestDir();
        string path = dir.Write("~tqexisting.tmp", [1]);

        var ex = Assert.Throws<FsException>(() => _fs.CreateTemp(path));

        Assert.True(ex.IsAlreadyExists);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void Write_flush_and_rename_publish_the_exact_content_and_times()
    {
        using var dir = new TestDir();
        var content = TestDir.RandomBytes(10_000);
        string temp = dir.PathOf("~tqtemp.tmp");
        string final = dir.PathOf("final.bin");

        UInt128 tempId;
        using (var file = _fs.CreateTemp(temp))
        {
            tempId = file.GetIdentity().FileId;
            file.Preallocate(1024 * 1024);
            file.Write(content, 0);
            file.SetLength(content.Length);
            file.SetTimesAndAttributes(SomeTime, SomeTime, 0);
            file.Flush();
            file.RenameTo(final, replaceExisting: false);
            file.Flush();
        }

        Assert.False(File.Exists(temp));
        Assert.Equal(content, File.ReadAllBytes(final));
        Assert.Equal(SomeTime, File.GetLastWriteTimeUtc(final).ToFileTimeUtc());
        Assert.Equal(SomeTime, File.GetCreationTimeUtc(final).ToFileTimeUtc());
        Assert.Equal(tempId, _fs.TryGetIdentity(final)!.Value.FileId);
    }

    [Fact]
    public void RenameTo_without_replace_refuses_an_existing_target_and_keeps_both()
    {
        using var dir = new TestDir();
        string final = dir.Write("final.bin", [7]);
        string temp = dir.PathOf("~tqtemp.tmp");

        using (var file = _fs.CreateTemp(temp))
        {
            file.Write([1, 2], 0);
            var ex = Assert.Throws<FsException>(() => file.RenameTo(final, replaceExisting: false));
            Assert.True(ex.IsAlreadyExists);
        }

        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(final));
        Assert.True(File.Exists(temp));
    }

    [Fact]
    public void RenameTo_with_replace_swaps_in_the_temp_file()
    {
        using var dir = new TestDir();
        string final = dir.Write("final.bin", [7]);
        var oldId = _fs.TryGetIdentity(final)!.Value.FileId;
        string temp = dir.PathOf("~tqtemp.tmp");

        using (var file = _fs.CreateTemp(temp))
        {
            file.Write([1, 2], 0);
            file.Flush();
            file.RenameTo(final, replaceExisting: true);
        }

        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(final));
        Assert.NotEqual(oldId, _fs.TryGetIdentity(final)!.Value.FileId);
    }

    [Fact]
    public void DeleteIfSameObject_deletes_only_the_matching_object()
    {
        using var dir = new TestDir();
        string a = dir.Write("a.bin", [1]);
        string b = dir.Write("b.bin", [2]);
        var bId = _fs.TryGetIdentity(b)!.Value.FileId;

        Assert.False(_fs.DeleteIfSameObject(a, bId));
        Assert.True(File.Exists(a));

        Assert.True(_fs.DeleteIfSameObject(b, bId));
        Assert.False(File.Exists(b));

        Assert.False(_fs.DeleteIfSameObject(b, bId));
    }

    [Fact]
    public void DeleteIfSameObject_removes_read_only_files()
    {
        using var dir = new TestDir();
        string path = dir.Write("ro.bin", [1]);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var id = _fs.TryGetIdentity(path)!.Value.FileId;

        Assert.True(_fs.DeleteIfSameObject(path, id));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void FlushIfSameObject_flushes_only_the_matching_object()
    {
        using var dir = new TestDir();
        string a = dir.Write("a.bin", [1]);
        string b = dir.Write("b.bin", [2]);
        UInt128 bId = _fs.TryGetIdentity(b)!.Value.FileId;

        Assert.False(_fs.FlushIfSameObject(a, bId));
        Assert.True(_fs.FlushIfSameObject(b, bId));
        Assert.False(_fs.FlushIfSameObject(dir.PathOf("missing.bin"), bId));
    }

    [Fact]
    public void FlushIfSameObject_returns_false_for_read_only_files()
    {
        using var dir = new TestDir();
        string path = dir.Write("ro.bin", [1]);
        UInt128 id = _fs.TryGetIdentity(path)!.Value.FileId;
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.False(_fs.FlushIfSameObject(path, id));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}
