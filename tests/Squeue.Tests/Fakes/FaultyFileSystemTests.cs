using Squeue.Core.FileSystem;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Fakes;

public class FaultyFileSystemTests
{
    [Fact]
    public void Crash_before_create_leaves_nothing()
    {
        using var dir = new TestDir();
        var fs = new FaultyFileSystem(new WindowsFileSystem()) { CrashAt = (FsOp.CreateTemp, false, 1) };

        Assert.Throws<SimulatedCrashException>(() => fs.CreateTemp(dir.PathOf("~tqaaaaaaaaaa.tmp")));

        Assert.True(fs.Crashed);
        Assert.False(File.Exists(dir.PathOf("~tqaaaaaaaaaa.tmp")));
    }

    [Fact]
    public void Crash_after_create_leaves_the_file_with_its_handle_closed()
    {
        using var dir = new TestDir();
        string path = dir.PathOf("~tqaaaaaaaaaa.tmp");
        var fs = new FaultyFileSystem(new WindowsFileSystem()) { CrashAt = (FsOp.CreateTemp, true, 1) };

        Assert.Throws<SimulatedCrashException>(() => fs.CreateTemp(path));

        Assert.True(File.Exists(path));
        File.Delete(path); // would fail with a sharing violation if the handle were still open
    }

    [Fact]
    public void Crash_fires_on_the_requested_occurrence_only_once()
    {
        using var dir = new TestDir();
        var fs = new FaultyFileSystem(new WindowsFileSystem()) { CrashAt = (FsOp.Write, false, 2) };
        using var temp = fs.CreateTemp(dir.PathOf("~tqaaaaaaaaaa.tmp"));

        temp.Write([1], 0);
        Assert.Throws<SimulatedCrashException>(() => temp.Write([2], 1));
        temp.Write([3], 1);
    }

    [Fact]
    public void CorruptVerifyReads_flips_the_first_byte()
    {
        using var dir = new TestDir();
        string path = dir.Write("a.bin", [10, 20, 30]);
        var fs = new FaultyFileSystem(new WindowsFileSystem()) { CorruptVerifyReads = true };

        using var file = fs.OpenForVerify(path);
        using var buffer = new AlignedBuffer(4096);
        int read = file.Read(buffer.Span, 0);

        Assert.Equal(3, read);
        Assert.Equal(new byte[] { (byte)(10 ^ 0xFF), 20, 30 }, buffer.Span[..3].ToArray());
    }
}
