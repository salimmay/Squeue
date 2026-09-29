using Squeue.ViewModels;

namespace Squeue.Tests.ViewModels;

public class FormatTests
{
    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "2 KB")]
    [InlineData(10 * 1048576L, "10 MB")]
    [InlineData(1610612736L, "1.5 GB")]
    public void Bytes(long value, string expected) => Assert.Equal(expected, Format.Bytes(value));

    [Theory]
    [InlineData(30, "under a minute left")]
    [InlineData(90, "2 min left")]
    [InlineData(3 * 3600 + 5 * 60, "3 h 5 min left")]
    public void TimeLeft(int seconds, string expected) => Assert.Equal(expected, Format.TimeLeft(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Speed_and_count() =>
        Assert.Equal(("10 MB/s", "1 file", "1,204 files"), (Format.Speed(10 * 1048576), Format.Count(1, "file"), Format.Count(1204, "file")));
}
