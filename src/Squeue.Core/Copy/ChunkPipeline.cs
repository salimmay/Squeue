using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Squeue.Core.FileSystem;

namespace Squeue.Core.Copy;

internal delegate int ReadChunk(Span<byte> buffer, long offset);
internal delegate void ChunkAction(ReadOnlySpan<byte> chunk);
internal delegate void ChunkConsumer(ReadOnlySpan<byte> chunk, long offset);

/// Reads a file on a dedicated reader thread while the calling thread consumes earlier chunks, so reading
/// overlaps writing or hashing. The buffers are pinned, 4096-aligned (unbuffered reads need that) and
/// allocated once, then reused for every run. Not for concurrent use.
internal sealed class ChunkPipeline
{
    private readonly Memory<byte>[] _buffers;

    public ChunkPipeline(int chunkSize, int depth)
    {
        if (chunkSize <= 0 || chunkSize % AlignedBuffer.Alignment != 0)
            throw new ArgumentException("Chunk size must be a positive multiple of 4096.", nameof(chunkSize));
        if (depth < 2) throw new ArgumentException("The pipeline needs at least two buffers.", nameof(depth));
        ChunkSize = chunkSize;
        Depth = depth;
        _buffers = new Memory<byte>[depth];
        for (int i = 0; i < depth; i++) _buffers[i] = AllocateAligned(chunkSize);
    }

    public int ChunkSize { get; }
    public int Depth { get; }

    /// Reads with <paramref name="read"/> until a zero-length read (or, with <paramref name="stopOnShortRead"/>, the
    /// first read shorter than a chunk) and hands the chunks in order to <paramref name="consume"/> on the calling
    /// thread. <paramref name="onRead"/> runs on the reading thread, in order. With <paramref name="overlap"/> false
    /// everything runs on the calling thread. Exceptions keep their type; the reader thread has always finished
    /// when this returns. Returns the bytes consumed.
    public long Run(ReadChunk read, ChunkAction? onRead, ChunkConsumer consume, bool stopOnShortRead, bool overlap,
        CancellationToken cancellationToken) =>
        overlap
            ? RunOverlapped(read, onRead, consume, stopOnShortRead, cancellationToken)
            : RunInline(read, onRead, consume, stopOnShortRead, cancellationToken);

    private long RunInline(ReadChunk read, ChunkAction? onRead, ChunkConsumer consume, bool stopOnShortRead,
        CancellationToken cancellationToken)
    {
        var buffer = _buffers[0].Span;
        long offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = read(buffer, offset);
            if (n == 0) break;
            onRead?.Invoke(buffer[..n]);
            consume(buffer[..n], offset);
            offset += n;
            if (stopOnShortRead && n < ChunkSize) break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return offset;
    }

    private long RunOverlapped(ReadChunk read, ChunkAction? onRead, ChunkConsumer consume, bool stopOnShortRead,
        CancellationToken cancellationToken)
    {
        using var free = new BlockingCollection<int>(Depth);
        using var filled = new BlockingCollection<(int Buffer, int Length, long Offset)>(Depth);
        for (int i = 0; i < Depth; i++) free.Add(i);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ExceptionDispatchInfo? readerError = null;

        var reader = new Thread(() =>
        {
            try
            {
                long offset = 0;
                while (true)
                {
                    int index = free.Take(stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                    var buffer = _buffers[index].Span;
                    int n = read(buffer, offset);
                    if (n == 0) break;
                    onRead?.Invoke(buffer[..n]);
                    filled.Add((index, n, offset), stop.Token);
                    offset += n;
                    if (stopOnShortRead && n < ChunkSize) break;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { readerError = ExceptionDispatchInfo.Capture(ex); }
            finally { filled.CompleteAdding(); }
        }) { IsBackground = true, Name = "Squeue reader" };

        long total = 0;
        reader.Start();
        try
        {
            foreach (var (index, length, offset) in filled.GetConsumingEnumerable(cancellationToken))
            {
                consume(_buffers[index].Span[..length], offset);
                total = offset + length;
                free.Add(index);
            }
        }
        finally
        {
            stop.Cancel(); // releases the reader if this side stopped early
            reader.Join();
        }

        readerError?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
        return total;
    }

    /// Pinned arrays never move, so an aligned slice stays aligned, and the GC frees them with the pipeline.
    private static unsafe Memory<byte> AllocateAligned(int size)
    {
        const int alignment = AlignedBuffer.Alignment;
        byte[] array = GC.AllocateUninitializedArray<byte>(size + alignment, pinned: true);
        fixed (byte* start = array)
        {
            int offset = (int)((alignment - ((nint)start & (alignment - 1))) & (alignment - 1));
            return array.AsMemory(offset, size);
        }
    }
}
