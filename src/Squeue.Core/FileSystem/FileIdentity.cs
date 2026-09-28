using System.Buffers.Binary;

namespace Squeue.Core.FileSystem;

/// Which file object this is (volume + file id) and which version of it (size, times).
/// Times are FILETIME values: 100 ns ticks since 1601-01-01 UTC.
public readonly record struct FileIdentity(
    ulong VolumeSerial,
    UInt128 FileId,
    long Size,
    long CreationTime,
    long LastWriteTime,
    long ChangeTime,
    uint Attributes)
{
    public bool IsSameObject(FileIdentity other) =>
        VolumeSerial == other.VolumeSerial && FileId == other.FileId;

    /// ChangeTime moves on any write or metadata change, even when an app restores LastWriteTime.
    public bool IsSameVersion(FileIdentity other) =>
        IsSameObject(other) && Size == other.Size && LastWriteTime == other.LastWriteTime && ChangeTime == other.ChangeTime;

    public static byte[] FileIdToBytes(UInt128 fileId)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(bytes, fileId);
        return bytes;
    }

    public static UInt128 FileIdFromBytes(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt128LittleEndian(bytes);
}
