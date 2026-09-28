using Squeue.Core.FileSystem;

namespace Squeue.Tests.FileSystem;

public class VerifyReadTests
{
    private const int Chunk = 64 * 1024;
    private readonly WindowsFileSystem _fs = new();

    [Fact]
    public void AlignedBuffer_rejects_unaligned_lengths()
    {
        Assert.Throws<ArgumentException>(() => new AlignedBuffer(1000));
        Assert.Throws<ArgumentException>(() => new AlignedBuffer(0));
        using var ok = new AlignedBuffer(Chunk);
        Assert.Equal(Chunk, ok.Span.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk)]
    public void Unbuffered_read_returns_exact_content(int size)
    {
        using var dir = new TestDir();
        var content = TestDir.RandomBytes(size);
        string path = dir.Write("a.bin", content);

        Assert.Equal(content, ReadAllUnbuffered(path));
    }

    [Fact]
    public void Can_verify_a_temp_file_while_its_handle_is_still_open()
    {
        using var dir = new TestDir();
        var content = TestDir.RandomBytes(Chunk + 10);
        string temp = dir.PathOf("~tqtemp.tmp");

        using var file = _fs.CreateTemp(temp);
        file.Write(content, 0);
        file.Flush();

        Assert.Equal(content, ReadAllUnbuffered(temp));
    }

    private byte[] ReadAllUnbuffered(string path)
    {
        using var file = _fs.OpenForVerify(path);
        using var buffer = new AlignedBuffer(Chunk);
        var result = new List<byte>();
        long offset = 0;
        while (true)
        {
            int read = file.Read(buffer.Span, offset);
            if (read == 0) break;
            result.AddRange(buffer.Span[..read].ToArray());
            offset += read;
            if (read < buffer.Length) break;
        }
        return result.ToArray();
    }
}
