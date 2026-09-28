using Squeue.Core.Copy;

namespace Squeue.Tests.Copy;

public class TempNameTests
{
    [Fact]
    public void Names_are_short_and_have_the_temp_shape()
    {
        string name = TempNames.New();
        Assert.Matches("^~tq[a-z2-7]{10}\\.tmp$", name);
        Assert.Equal(17, name.Length);
        Assert.True(TempNames.HasTempShape(name));
    }

    [Fact]
    public void Names_are_unique()
    {
        var names = Enumerable.Range(0, 10_000).Select(_ => TempNames.New()).ToHashSet();
        Assert.Equal(10_000, names.Count);
    }

    [Theory]
    [InlineData("photo.tmp")]
    [InlineData("~tqABCDEFGHIJ.tmp")]
    [InlineData("~tqabc.tmp")]
    public void Other_names_do_not_have_the_temp_shape(string name) =>
        Assert.False(TempNames.HasTempShape(name));
}
