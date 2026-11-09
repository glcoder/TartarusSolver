using Tartarus.Vision;

namespace Tartarus.Rules.Tests;

public class WindowsSeenTests
{
    [Fact]
    public void The_game_s_windows_are_told_whole_and_the_others_only_by_process()
    {
        string text = WindowsSeen.Describe([
            new("torchlight_infinite", "", false, false, false, false),
            new("TapTap", "\u706b\u70ac\u4e4b\u5149", true, false, false, true),
            new("msedge", "My bank - private", true, false, false, false),
            new("msedge", "Mail", true, false, false, false),
            new("secret_hidden", "Hidden", false, false, false, false),
            new("cloaked_one", "On another desktop", true, true, false, false),
        ]);
        Assert.Equal(new[]
        {
            "torchlight_infinite \"\"",
            "TapTap \"\u706b\u70ac\u4e4b\u5149\" visible minimized",
            "Other visible windows: msedge x2",
        }, text.Split(Environment.NewLine));
    }

    [Fact]
    public void No_game_window_is_said() =>
        Assert.StartsWith("No window of the game", WindowsSeen.Describe([]));
}
