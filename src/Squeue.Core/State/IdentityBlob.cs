using System.Buffers.Binary;
using Squeue.Core.FileSystem;

namespace Squeue.Core.State;

/// Stores a FileIdentity as 60 bytes: volume, file id, size, creation, last write, change, attributes.
internal static class IdentityBlob
{
    public static byte[]? Encode(FileIdentity? identity)
    {
        if (identity is not { } id) return null;
        var bytes = new byte[60];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0), id.VolumeSerial);
        BinaryPrimitives.WriteUInt128LittleEndian(bytes.AsSpan(8), id.FileId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), id.Size);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), id.CreationTime);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40), id.LastWriteTime);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(48), id.ChangeTime);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), id.Attributes);
        return bytes;
    }

    public static FileIdentity? Decode(byte[]? bytes) => bytes is null
        ? null
        : new FileIdentity(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0)),
            BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(8)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(40)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(48)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(56)));
}
