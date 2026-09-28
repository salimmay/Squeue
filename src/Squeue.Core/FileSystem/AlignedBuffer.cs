using System.Runtime.InteropServices;

namespace Squeue.Core.FileSystem;

/// Native memory aligned to 4096 bytes, as unbuffered (FILE_FLAG_NO_BUFFERING) reads require.
public sealed unsafe class AlignedBuffer : IDisposable
{
    public const int Alignment = 4096;
    private void* _memory;

    public AlignedBuffer(int length)
    {
        if (length <= 0 || length % Alignment != 0)
            throw new ArgumentException("Length must be a positive multiple of 4096.", nameof(length));
        _memory = NativeMemory.AlignedAlloc((nuint)length, Alignment);
        Length = length;
    }

    public int Length { get; }

    public Span<byte> Span => _memory == null
        ? throw new ObjectDisposedException(nameof(AlignedBuffer))
        : new Span<byte>(_memory, Length);

    public void Dispose()
    {
        if (_memory == null) return;
        NativeMemory.AlignedFree(_memory);
        _memory = null;
    }
}
