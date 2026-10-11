using Squeue.ViewModels;

namespace Squeue.Tests.ViewModels;

public class LaunchTests
{
    [Fact]
    public void Plain_paths_are_sources_with_no_destination()
    {
        var r = LaunchRequest.Parse([@"G:\DCIM", @"C:\photo.jpg"]);
        Assert.Equal([@"G:\DCIM", @"C:\photo.jpg"], r.Sources);
        Assert.Null(r.Destination);
        Assert.False(r.Paste);
    }

    [Fact]
    public void To_sets_the_destination()
    {
        var r = LaunchRequest.Parse(["--to", @"E:\Backup", @"G:\DCIM"]);
        Assert.Equal(@"E:\Backup", r.Destination);
        Assert.Equal([@"G:\DCIM"], r.Sources);
    }

    [Fact]
    public void Paste_takes_the_folder_and_no_sources()
    {
        var r = LaunchRequest.Parse(["--PASTE", @"E:\Backup"]);
        Assert.True(r.Paste);
        Assert.Equal(@"E:\Backup", r.Destination);
        Assert.Empty(r.Sources);
        Assert.Throws<ArgumentException>(() => LaunchRequest.Parse(["--paste", @"E:\Backup", @"G:\DCIM"]));
    }

    [Fact]
    public void A_drive_root_mangled_by_quoting_is_repaired()
    {
        // Explorer's "%1" for G:\ arrives as G:" because \" escapes the quote.
        Assert.Equal([@"G:\"], LaunchRequest.Parse(["G:\""]).Sources);
        Assert.Equal(@"E:\", LaunchRequest.Parse(["--paste", "E:\""]).Destination);
    }

    [Fact]
    public void Bad_options_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => LaunchRequest.Parse(["--to"]));
        Assert.Throws<ArgumentException>(() => LaunchRequest.Parse(["--move", "a"]));
    }

    [Fact]
    public void No_arguments_is_empty()
    {
        Assert.True(LaunchRequest.Parse([]).IsEmpty);
        Assert.True(LaunchRequest.Parse(["", "  "]).IsEmpty);
    }

    [Fact]
    public void One_launch_per_selected_item_merges_into_one_request()
    {
        var merged = LaunchRequest.Merge([
            LaunchRequest.Parse([@"G:\DCIM\100CANON"]),
            LaunchRequest.Parse([@"G:\DCIM\101CANON"]),
            LaunchRequest.Parse([@"g:\dcim\100canon"]), // same item twice
        ]);
        var only = Assert.Single(merged);
        Assert.Equal([@"G:\DCIM\100CANON", @"G:\DCIM\101CANON"], only.Sources);
    }

    [Fact]
    public void Different_destinations_and_pastes_stay_apart()
    {
        var merged = LaunchRequest.Merge([
            LaunchRequest.Parse(["--to", @"E:\A", "x"]),
            LaunchRequest.Parse(["--to", @"E:\B", "y"]),
            LaunchRequest.Parse(["--paste", @"E:\A"]),
            LaunchRequest.Parse(["--paste", @"e:\a"]),
            LaunchRequest.Parse(["z"]),
            LaunchRequest.Nothing,
        ]);
        Assert.Equal(4, merged.Count);
        Assert.Single(merged, m => m.Paste);
        Assert.Contains(merged, m => m.Destination is null && m.Sources.SequenceEqual(["z"]));
    }
}

public class CommandChannelTests
{
    [Fact]
    public void A_second_launch_hands_its_arguments_to_the_running_one()
    {
        string name = "Squeue.Test." + Guid.NewGuid().ToString("N");
        var got = new System.Collections.Concurrent.BlockingCollection<string[]>();
        using (var channel = new CommandChannel(name))
        {
            channel.Listen(got.Add);
            Assert.True(CommandChannel.TrySend(name, ["--paste", @"E:\Café 写真"], TimeSpan.FromSeconds(5)));
            Assert.True(CommandChannel.TrySend(name, [@"G:\DCIM"], TimeSpan.FromSeconds(5)));
            Assert.True(got.TryTake(out var first, TimeSpan.FromSeconds(5)));
            Assert.True(got.TryTake(out var second, TimeSpan.FromSeconds(5)));
            Assert.Equal(["--paste", @"E:\Café 写真"], first);
            Assert.Equal([@"G:\DCIM"], second);
        }
    }

    [Fact]
    public void Sending_with_nobody_listening_gives_up()
    {
        Assert.False(CommandChannel.TrySend("Squeue.Test." + Guid.NewGuid().ToString("N"), ["x"], TimeSpan.FromMilliseconds(300)));
    }
}
