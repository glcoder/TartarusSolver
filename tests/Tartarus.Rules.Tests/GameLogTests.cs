using Tartarus.Vision;

namespace Tartarus.Rules.Tests;

public class GameLogTests
{
    private static string Page(string time, string what) => $"[2026.10.27-{time}:000][ 46]TLShipping: Display: [Game] Page@ {what}";

    private static IEnumerable<string> Day(string time) => [Page(time, "OpenView S16BoardNewDayPop"), Page(time, "CloseView 3490_S16BoardNewDayPopCtrl")];

    private static IEnumerable<string> Chromatic(string time) =>
    [
        $"[2026.10.27-{time}:000][120]TLShipping: Display: [Game] AnimationMgr@ Animation not found! Name = Anim_Archive_LightUp, Id = 3495_S16BoardArchiveCell_0000025D",
        Page(time, "OpenView S16BoardColoredPop"),
        Page(time, "CloseView 3535_S16BoardColoredPopCtrl"),
    ];

    [Fact]
    public void A_Warden_s_Treasure_taken_changes_the_Sandbox()
    {
        // 2026-10-28: two Chromatics, two Treasures; the first (Three-Headed Thumb Ring) opened three cells, and the
        // second was hinted over the Sandbox from before it.
        var log = new GameLog();
        foreach (var line in new[]
        {
            "[2026.10.27-22.40.00:000][674]TLGame: Display: [Game] UIButton@ normal-click Btn_S16GamePlay_Start",
            "[2026.10.27-22.40.01:000][  1]TLLua: Display: [S16LastDay] check cur=0 max=20",
            "[2026.10.27-22.55.14:156][961]TLLua: Display: [Game] S16BoardColoredPop Run",
            "[2026.10.27-22.55.19:580][  7]TLGame: Display: [Game] UIButton@ normal-click Btn_S16Board_ColoredPop_Confirm",
        }) log.Feed(line);
        var run = Assert.Single(log.Runs);
        var confirm = new DateTime(2026, 10, 27, 22, 55, 19, 580, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(confirm, run.BoardChangedAt);
        Assert.Equal(1, run.Treasures);
    }

    [Fact]
    public void A_Chromatic_s_flight_is_no_change_of_its_own()
    {
        // 2026-10-28: the cell was empty before the flight's sound; a reading after it like those before passed for the
        // old Sandbox, and the eye button was asked for again and again over the Warden's Treasure.
        var log = new GameLog();
        foreach (var line in new[]
        {
            "[2026.10.27-23.30.00:000][674]TLGame: Display: [Game] UIButton@ normal-click Btn_S16GamePlay_Start",
            "[2026.10.27-23.30.01:000][  1]TLLua: Display: [S16LastDay] check cur=0 max=30",
            "[2026.10.27-23.40.47:476][645]TLLua: Display: [Game] AudioMgr@PlayAudio  AudioEnum  == UI_S16_Premium_Quality",
            "[2026.10.27-23.40.49:149][770]TLLua: Display: [Game] AudioMgr@PlayAudio  AudioEnum  == UI_S16_Premium_Quality_Fly",
        }) log.Feed(line);
        var run = Assert.Single(log.Runs);
        Assert.Equal(new DateTime(2026, 10, 27, 23, 40, 47, 476, DateTimeKind.Utc).ToLocalTime(), run.BoardChangedAt);
        Assert.Equal(1, run.Chromatic);
    }

    [Fact]
    public void A_run_ends_at_its_first_result_screen()
    {
        // The statistics' time in runs: from the Start button to the Riot's result, which may come up twice.
        var log = new GameLog();
        foreach (var line in new[]
        {
            "[2026.10.26-23.35.58:091][674]TLGame: Display: [Game] UIButton@ normal-click Btn_S16GamePlay_Start",
            "[2026.10.26-23.36.01:000][  1]TLLua: Display: [S16LastDay] check cur=0 max=20",
            "[2026.10.26-23.52.52:391][566]TLLua: Display: [Game] S16GamePlayCheckOut Run",
            "[2026.10.26-23.53.58:020][650]TLLua: Display: [Game] S16GamePlayCheckOut Run",
        }) log.Feed(line);
        var run = Assert.Single(log.Runs);
        Assert.True(run is { FromStart: true, Finished: true });
        Assert.Equal(TimeSpan.FromSeconds(16 * 60 + 54), run.End!.Value - run.Start);
    }

    [Fact]
    public void A_log_of_pages_tells_the_runs_days_and_Chromatics()
    {
        // 2026-10-27, a player's report: their client logs the pages it opens, not the sounds, clicks and day counter the
        // log was read by. No run was told, the run's memory never started over, and the next run began with the last
        // one's 10 Chromatics: every hint was for chests alone.
        var log = new GameLog();
        var lines = new List<string>
        {
            Page("04.45.07", "OpenView S16GamePlayEntry"),
            Page("04.45.57", "OpenView S16BoardThemePop"),
            Page("04.46.00", "OpenView S16BoardTaskPop"),
            Page("04.46.03", "OpenView S16BoardMain"),
            Page("04.46.04", "OpenView S16BoardRelicPop"),
            Page("04.46.08", "CloseView 3482_S16BoardRelicPopCtrl"),
        };
        for (int d = 0; d < 20; d++)
        {
            lines.AddRange(Day($"04.{47 + d / 3:00}.{d % 3 * 20:00}"));
            if (d < 10) lines.AddRange(Chromatic($"04.{47 + d / 3:00}.{d % 3 * 20 + 5:00}"));
        }
        lines.Add(Page("04.48.00", "OpenView S16BoardEventPop"));
        lines.Add(Page("04.55.10", "OpenView S16GamePlayCheckOut"));
        foreach (var line in lines) log.Feed(line);

        var first = Assert.Single(log.Runs);
        Assert.True(first is { Paged: true, FromStart: true, ThemeShown: true, Finished: true });
        Assert.Equal((20, 10, 10, 10, 1, 1), (first.Day, first.Chromatic, first.Treasures, first.ChromaticAtLeast, first.Incidents, first.InstrumentPicks));
        // A theme's run: its length is the theme's (Iron Law 30), which the log doesn't name.
        Assert.Equal(0, first.MaxDays);
        Assert.Equal(new DateTime(2026, 10, 27, 4, 55, 10), first.End);

        // Back in the Tartarus: the board and the last result show again, then the entry screen and a run with no theme.
        foreach (var line in new[]
        {
            Page("05.11.37", "OpenView S16BoardMain"),
            Page("05.11.38", "OpenView S16GamePlayCheckOut"),
            Page("05.11.40", "OpenView S16GamePlayEntry"),
            Page("05.11.44", "OpenView S16BoardTaskPop"),
            Page("05.11.48", "OpenView S16BoardRelicPop"),
        }) log.Feed(line);
        Assert.Equal(2, log.Runs.Count);
        var second = log.Current!;
        Assert.True(second is { FromStart: true, ThemeShown: false, Finished: false, PickOpen: true, Chromatic: 0, ChromaticAtLeast: 0 });
        Assert.Equal(20, second.MaxDays);
        log.Feed(Page("05.11.55", "CloseView 7844_S16BoardRelicPopCtrl"));
        for (int d = 0; d < 6; d++) foreach (var line in Day($"05.12.{d * 5:00}")) log.Feed(line);
        Assert.Equal((6, 14, false), (second.Day, second.RemainingDays, second.PickOpen));
    }

    [Fact]
    public void A_log_with_the_day_counter_goes_by_it_not_by_pages()
    {
        var log = new GameLog();
        log.Feed("[2026.10.26-19.51.05:401][265]TLLua: Display: [Game] [S16LastDay] check cur=0 max=0 played=false");
        log.Feed("[2026.10.26-19.51.10:000][300]TLShipping: Display: [Game] UIButton@ normal-click Btn_S16GamePlay_Start");
        log.Feed("[2026.10.26-19.51.17:209][431]TLLua: Display: [Game] [S16LastDay] check cur=0 max=20 played=false");
        log.Feed(Page("19.51.20", "OpenView S16BoardNewDayPop"));
        log.Feed("[2026.10.26-19.51.20:432][ 60]TLLua: Display: [Game] [S16LastDay] check cur=1 max=20 played=true");
        var run = Assert.Single(log.Runs);
        Assert.False(run.Paged);
        Assert.Equal((1, 19), (run.Day, run.RemainingDays));
    }

    [Fact]
    public void Without_a_log_more_Remaining_Days_start_a_new_run()
    {
        var memory = new RunMemory();
        var t = DateTimeOffset.Now;
        memory.Days(5);
        memory.ChromaticCount(10, false, t);
        memory.ChromaticCount(10, false, t + RunMemory.ChromaticHold);
        Assert.Equal(10, memory.Chromatic);
        memory.Days(15);
        Assert.Equal((0, 15), (memory.Chromatic, memory.LastDays));
    }
}
