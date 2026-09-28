using System.IO.Hashing;
using System.Security.Cryptography;

namespace Squeue.Core.Copy;

public interface IContentHasher
{
    void Append(ReadOnlySpan<byte> data);

    /// Lowercase hex of the hash of everything appended.
    string FinishHex();
}

public static class Hashers
{
    public static readonly string[] Supported = ["xxh3", "xxh128", "crc32", "md5", "sha1", "sha256"];

    public static IContentHasher Create(string algorithm) => algorithm switch
    {
        "xxh3" => new Xxh3Hasher(),
        "xxh128" => new Xxh128Hasher(),
        "crc32" => new Crc32Hasher(),
        "md5" => new CryptoHasher(HashAlgorithmName.MD5),
        "sha1" => new CryptoHasher(HashAlgorithmName.SHA1),
        "sha256" => new CryptoHasher(HashAlgorithmName.SHA256),
        _ => throw new ArgumentException($"Unknown hash algorithm '{algorithm}'.", nameof(algorithm)),
    };

    private sealed class Xxh3Hasher : IContentHasher
    {
        private readonly XxHash3 _hash = new();
        public void Append(ReadOnlySpan<byte> data) => _hash.Append(data);
        public string FinishHex() => _hash.GetCurrentHashAsUInt64().ToString("x16");
    }

    private sealed class Xxh128Hasher : IContentHasher
    {
        private readonly XxHash128 _hash = new();
        public void Append(ReadOnlySpan<byte> data) => _hash.Append(data);
        public string FinishHex() => _hash.GetCurrentHashAsUInt128().ToString("x32");
    }

    private sealed class Crc32Hasher : IContentHasher
    {
        private readonly Crc32 _hash = new();
        public void Append(ReadOnlySpan<byte> data) => _hash.Append(data);
        public string FinishHex() => _hash.GetCurrentHashAsUInt32().ToString("x8");
    }

    private sealed class CryptoHasher(HashAlgorithmName name) : IContentHasher
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(name);
        public void Append(ReadOnlySpan<byte> data) => _hash.AppendData(data);
        public string FinishHex() => Convert.ToHexStringLower(_hash.GetHashAndReset());
    }
}
