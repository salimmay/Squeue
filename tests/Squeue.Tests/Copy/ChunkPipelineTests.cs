using Squeue.Core.Copy;

namespace Squeue.Tests.Copy;

public class ChunkPipelineTests
{
    private const int Chunk = 4096;

    private static ReadChunk ReaderOver(byte[] data, Action<long>? beforeRead = null) => (buffer, offset) =>
    {
        beforeRead?.Invoke(offset);
        int n = (int)Math.Min(buffer.Length, data.Length - offset);
        data.AsSpan((int)offset, n).CopyTo(buffer);
        return n;
    };

    [Theory]
    [InlineData(0, 2, true)]
    [InlineData(1, 2, true)]
    [InlineData(Chunk - 1, 4, true)]
    [InlineData(Chunk, 2, true)]
    [InlineData(Chunk + 1, 2, true)]
    [InlineData(10 * Chunk + 7, 2, true)]
    [InlineData(10 * Chunk + 7, 4, true)]
    [InlineData(10 * Chunk + 7, 2, false)]
    public void Delivers_every_chunk_in_order_with_exact_content(int size, int depth, bool overlap)
    {
        var data = TestDir.RandomBytes(size);
        var output = new MemoryStream();
        long expectedOffset = 0;

        long total = new ChunkPipeline(Chunk, depth).Run(ReaderOver(data), null, (chunk, offset) =>
        {
            Assert.Equal(expectedOffset, offset);
            output.Write(chunk);
            expectedOffset += chunk.Length;
        }, stopOnShortRead: false, overlap, CancellationToken.None);

        Assert.Equal(size, total);
        Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public void OnRead_sees_every_chunk_in_order()
    {
        var data = TestDir.RandomBytes(7 * Chunk + 3);
        var hasher = Hashers.Create("xxh3");

        new ChunkPipeline(Chunk, 2).Run(ReaderOver(data), chunk => hasher.Append(chunk), (_, _) => { },
            stopOnShortRead: false, overlap: true, CancellationToken.None);

        var expected = Hashers.Create("xxh3");
        expected.Append(data);
        Assert.Equal(expected.FinishHex(), hasher.FinishHex());
    }

    [Fact]
    public void StopOnShortRead_ends_at_the_first_short_read()
    {
        var data = TestDir.RandomBytes(3 * Chunk + 10);
        int reads = 0;

        long total = new ChunkPipeline(Chunk, 2).Run(ReaderOver(data, _ => reads++), null, (_, _) => { },
            stopOnShortRead: true, overlap: true, CancellationToken.None);

        Assert.Equal(data.Length, total);
        Assert.Equal(4, reads);
    }

    [Fact]
    public void A_reader_error_is_rethrown_with_its_type_after_the_reader_stops()
    {
        var data = TestDir.RandomBytes(10 * Chunk);
        var consumed = new List<long>();
        int readerThread = 0;

        var ex = Assert.Throws<IOException>(() => new ChunkPipeline(Chunk, 2).Run(
            ReaderOver(data, offset =>
            {
                readerThread = Environment.CurrentManagedThreadId;
                if (offset == 3 * Chunk) throw new IOException("card removed");
            }),
            null, (_, offset) => consumed.Add(offset), stopOnShortRead: false, overlap: true, CancellationToken.None));

        Assert.Equal("card removed", ex.Message);
        Assert.Equal(new long[] { 0, Chunk, 2 * Chunk }, consumed);
        Assert.NotEqual(Environment.CurrentManagedThreadId, readerThread);
    }

    [Fact]
    public void A_consumer_error_stops_the_reader_and_is_rethrown()
    {
        var data = TestDir.RandomBytes(100 * Chunk);
        int reads = 0;

        Assert.Throws<InvalidOperationException>(() => new ChunkPipeline(Chunk, 2).Run(
            ReaderOver(data, _ => Interlocked.Increment(ref reads)), null,
            (_, offset) => { if (offset == Chunk) throw new InvalidOperationException("disk full"); },
            stopOnShortRead: false, overlap: true, CancellationToken.None));

        int after = Volatile.Read(ref reads);
        Assert.True(after <= 5, $"The reader kept going after the consumer failed ({after} reads).");
    }

    [Fact]
    public void Cancelling_mid_run_throws_and_stops_the_reader()
    {
        var data = TestDir.RandomBytes(100 * Chunk);
        using var stop = new CancellationTokenSource();
        int reads = 0;

        Assert.ThrowsAny<OperationCanceledException>(() => new ChunkPipeline(Chunk, 2).Run(
            ReaderOver(data, _ => Interlocked.Increment(ref reads)), null,
            (_, offset) => { if (offset == Chunk) stop.Cancel(); },
            stopOnShortRead: false, overlap: true, stop.Token));

        Assert.True(Volatile.Read(ref reads) <= 5);
    }

    [Fact]
    public void The_pipeline_can_be_run_again_after_a_failure()
    {
        var pipeline = new ChunkPipeline(Chunk, 2);
        var data = TestDir.RandomBytes(5 * Chunk);
        Assert.Throws<IOException>(() => pipeline.Run(ReaderOver(data, o => { if (o == Chunk) throw new IOException("x"); }),
            null, (_, _) => { }, false, true, CancellationToken.None));

        var output = new MemoryStream();
        pipeline.Run(ReaderOver(data), null, (chunk, _) => output.Write(chunk), false, true, CancellationToken.None);

        Assert.Equal(data, output.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_already_cancelled_token_throws_on_both_paths(bool overlap)
    {
        var data = TestDir.RandomBytes(3 * Chunk);
        var pipeline = new ChunkPipeline(Chunk, 2);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool consumed = false;

        Assert.ThrowsAny<OperationCanceledException>(() =>
            pipeline.Run(ReaderOver(data), null, (_, _) => consumed = true, false, overlap, cts.Token));

        Assert.False(consumed);
    }

    [Fact]
    public void Rejects_bad_sizes()
    {
        Assert.Throws<ArgumentException>(() => new ChunkPipeline(1000, 2));
        Assert.Throws<ArgumentException>(() => new ChunkPipeline(Chunk, 1));
    }
}
