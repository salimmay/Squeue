using System.Text;
using Squeue.Core.Copy;

namespace Squeue.Tests.Copy;

public class HasherTests
{
    [Theory]
    [InlineData("xxh3", "", "2d06800538d394c2")]
    [InlineData("xxh128", "", "99aa06d3014798d86001c324468d497f")]
    [InlineData("crc32", "123456789", "cbf43926")]
    [InlineData("md5", "", "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("sha1", "abc", "a9993e364706816aba3e25717850c26c9cd0d89d")]
    [InlineData("sha256", "abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void Known_vectors(string algorithm, string input, string expected)
    {
        var hasher = Hashers.Create(algorithm);
        hasher.Append(Encoding.ASCII.GetBytes(input));
        Assert.Equal(expected, hasher.FinishHex());
    }

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void Chunked_input_hashes_the_same_as_one_block(string algorithm)
    {
        var data = TestDir.RandomBytes(1_000_003);
        var whole = Hashers.Create(algorithm);
        whole.Append(data);

        var chunked = Hashers.Create(algorithm);
        int offset = 0;
        foreach (int size in new[] { 1, 4095, 65536, 7, 300_000 })
        {
            chunked.Append(data.AsSpan(offset, size));
            offset += size;
        }
        chunked.Append(data.AsSpan(offset));

        Assert.Equal(whole.FinishHex(), chunked.FinishHex());
    }

    [Fact]
    public void Unknown_algorithm_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Hashers.Create("blake9"));

    public static IEnumerable<object[]> AllAlgorithms() => Hashers.Supported.Select(a => new object[] { a });
}
