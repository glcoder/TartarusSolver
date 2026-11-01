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
    public void Opulence_s_three_picks_of_one_Warden_s_Treasure_are_one_Chromatic()
    {
        // 2026-10-30, a report: Opulence, and the server slow to answer (picks came up again); 12 Confirms for 4
        // Chromatics were read as 10, and the hints played as if the Chromatic panel were full.
        var log = new GameLog();
        log.Feed("[2026.10.30-18.58.31:493][845]TLGame: Display: [Game] UIButton@ normal-click Btn_S16GamePlay_Start");
        log.Feed("[2026.10.30-18.58.32:000][  1]TLLua: Display: [Game] [S16LastDay] check cur=0 max=20");
        log.Feed("[2026.10.30-19.13.42:383][221]TLLua: Display: [Game] AudioMgr@PlayAudio  AudioEnum  == UI_S16_Premium_Quality");
        log.Feed("[2026.10.30-19.13.43:068][330]TLShipping: Display: [Game] AnimationMgr@ Animation not found! Name = Anim_Archive_LightUp, Id = 51531_S16BoardArchiveCell_000002024D29EFC8_Anim_Archive_LightUp");
        for (int i = 0; i < 5; i++)
        {
            log.Feed($"[2026.10.30-19.13.{43 + 2 * i}:393][374]TLLua: Display: [Game] S16BoardColoredPop Run");
            log.Feed($"[2026.10.30-19.13.{44 + 2 * i}:451][177]TLGame: Display: [Game] UIButton@ normal-click Btn_S16Board_ColoredPop_Confirm");
        }
        var run = Assert.Single(log.Runs);
        Assert.Equal((1, 1), (run.Chromatic, run.ChromaticAtLeast));
    }

    [Fact]
    public void The_eye_button_s_look_lasts_from_its_click_to_the_way_back()
    {
        // 2026-10-31: the pick's popup reaches the log a moment after the screen, so "back to the pick" flashed over the
        // Sandbox as each Warden's Treasure came up and was taken; the eye button's own clicks say when it's a look.
        var log = new GameLog();
        log.Feed("[2026.10.30-21.05.01:741][  1]TLGame: Display: [Game] UIButton@ normal-click Btn_S16GamePlay_Start");
        log.Feed("[2026.10.30-21.05.02:000][  1]TLLua: Display: [Game] [S16LastDay] check cur=0 max=20");
        log.Feed("[2026.10.30-21.11.51:134][ 43]TLLua: Display: [Game] S16BoardColoredPop Run");
        var run = Assert.Single(log.Runs);
        Assert.True(run is { PickOpen: true, Looking: false });
        log.Feed("[2026.10.30-21.11.55:814][809]TLGame: Display: [Game] UIButton@ normal-click S16ViewBoardBtn_ColoredPop");
        Assert.True(run.Looking);
        log.Feed("[2026.10.30-21.11.56:958][ 18]TLLua: Display: [Game] S16BoardColoredPop Run");
        log.Feed("[2026.10.30-21.11.56:962][ 18]TLGame: Display: [Game] UIButton@ normal-click Btn_S16Board_ViewReturn");
        Assert.True(run is { PickOpen: true, Looking: false });
        log.Feed("[2026.10.30-21.11.59:542][436]TLGame: Display: [Game] UIButton@ normal-click Btn_S16Board_ColoredPop_Confirm");
        Assert.True(run is { PickOpen: false } && run.PickTakenAt == run.BoardChangedAt && run.PickTakenAt != default);
    }

    [Fact]
    public void A_pick_the_game_clicks_for_the_player_is_closed()
    {
        // 2026-10-31, a player's report: the first instrument pick taken by "sim-click" stayed open, and every Sandbox
        // read asked to go back to it (the eye button framed instead of the Destination hinted).
        var log = new GameLog();
        log.Feed("[2026.10.30-20.45.14:903][859]TLGame: Display: [Game] UIButton@ normal-click Btn_S16GamePlay_Start");
        log.Feed("[2026.10.30-20.45.19:174][533]TLLua: Display: [Game] [S16LastDay] check cur=0 max=20 played=false");
        log.Feed("[2026.10.30-20.45.19:182][533]TLLua: Display: [Game] S16BoardRelicPop Run");
        log.Feed("[2026.10.30-20.45.22:285][ 30]TLGame: Display: [Game] UIButton@ sim-click Btn_S16Board_RelicPop_Confirm");
        var run = Assert.Single(log.Runs);
        Assert.False(run.PickOpen);
        // Whatever closed it, a new day has no pick open.
        log.Feed("[2026.10.30-20.46.00:000][  1]TLLua: Display: [Game] S16BoardRelicPop Run");
        log.Feed("[2026.10.30-20.46.05:000][  1]TLLua: Display: [Game] [S16LastDay] check cur=1 max=20 played=false");
        Assert.False(run.PickOpen);
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
    public void A_look_at_the_Sandbox_from_a_Warden_s_Treasure_is_no_new_Chromatic()
    {
        // 2026-10-30, a 4K player's reports: the eye button closes the pick's page and opens it again; every look
        // counted a Chromatic and a change of the Sandbox, and the pick asked for the eye button again and again.
        var log = new GameLog();
        foreach (var line in new[]
        {
            Page("04.45.07", "OpenView S16GamePlayEntry"),
            Page("04.46.04", "OpenView S16BoardRelicPop"),
            Page("04.46.08", "CloseView 3482_S16BoardRelicPopCtrl"),
        }.Concat(Chromatic("04.50.00").Take(2))) log.Feed(line);
        var run = Assert.Single(log.Runs);
        var shown = run.BoardChangedAt;
        for (int look = 0; look < 3; look++)
        {
            log.Feed(Page($"04.50.{10 + look * 5:00}", "CloseView 3535_S16BoardColoredPopCtrl"));
            log.Feed(Page($"04.50.{12 + look * 5:00}", "OpenView S16BoardColoredPop"));
        }
        Assert.True(run is { Chromatic: 1, Treasures: 0, PickOpen: true, TreasureOpen: true });
        Assert.Equal(shown, run.BoardChangedAt);
        log.Feed(Page("04.51.00", "CloseView 3535_S16BoardColoredPopCtrl"));
        Assert.True(run is { Chromatic: 1, Treasures: 1, PickOpen: false });
        // The next Chromatic lights its slot first: a new pick.
        foreach (var line in Chromatic("04.52.00")) log.Feed(line);
        Assert.True(run is { Chromatic: 2, Treasures: 2, ChromaticAtLeast: 2 });
        Assert.True(run.BoardChangedAt > shown);

        // The Objective's instrument pick looked away from and back to is still the one pick.
        foreach (var line in new[]
        {
            Page("04.53.00", "OpenView S16BoardRelicPop"),
            Page("04.53.10", "CloseView 3600_S16BoardRelicPopCtrl"),
            Page("04.53.12", "OpenView S16BoardRelicPop"),
        }) log.Feed(line);
        Assert.True(run is { InstrumentPicks: 2, PickOpen: true });
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

    [Fact]
    public void A_fade_s_misread_doesn_t_change_the_run_s_instruments_or_Objective()
    {
        // 2026-11-01, a report: as a Warden's Treasure opened, Kin's Coin of Fate read as Ravenous Chains and the done
        // Behind Cell Doors as Survival of the Fittest 0/4, and the pick was hinted for them.
        var memory = new RunMemory();
        Assert.Equal(["coin", "axe"], memory.Instruments(["coin", "axe"]));
        Assert.Equal(["coin", "axe"], memory.Instruments(["chains", "axe"]));
        Assert.Equal(["coin", "axe"], memory.Instruments(["chains", "axe"]));
        // A new slot counts at once.
        Assert.Equal(["coin", "axe", "saw"], memory.Instruments(["coin", "axe", "saw"]));

        var done = new Observation { Objective = ObjectiveKind.BehindCellDoors, ObjectiveProgress = 10, ObjectiveDone = true };
        memory.Objective(done);
        var fade = new Observation { Objective = ObjectiveKind.SurvivalOfTheFittest, ObjectiveProgress = 0 };
        memory.Objective(fade);
        Assert.Equal((ObjectiveKind.BehindCellDoors, 10, true), (fade.Objective, fade.ObjectiveProgress, fade.ObjectiveDone));
        // Readings searched ahead are told the run's Objective without counting toward a change.
        for (int i = 0; i < RunMemory.ChangeAfter + 1; i++)
        {
            var ahead = new Observation { Objective = ObjectiveKind.SurvivalOfTheFittest };
            memory.Objective(ahead, settled: false);
            Assert.Equal(ObjectiveKind.BehindCellDoors, ahead.Objective);
        }
        var unread = new Observation();
        memory.Objective(unread);
        Assert.Equal((ObjectiveKind.BehindCellDoors, true), (unread.Objective, unread.ObjectiveDone));
        // Read so again and again, it is the run's (the first one was the misread).
        for (int i = 0; i < RunMemory.ChangeAfter; i++)
        {
            var again = new Observation { Objective = ObjectiveKind.PrisonWalk, ObjectiveProgress = 4 };
            memory.Objective(again);
            if (i == RunMemory.ChangeAfter - 1) Assert.Equal(ObjectiveKind.PrisonWalk, again.Objective);
        }
    }

    [Fact]
    public void A_Replace_is_told_by_its_new_Inmate_s_reveal_not_a_Consume_or_an_End()
    {
        // 2026-10-31: a decision card's click, then the first telling sound: a new Inmate's reveal (Replace), the red
        // screen (Consume) or the new day (End).
        var log = new GameLog();
        log.Feed("[2026.10.31-22.05.51:713][156]TLGame: Display: [Game] UIButton@ normal-click Btn_S16GamePlay_Start");
        log.Feed("[2026.10.31-22.05.52:000][  1]TLLua: Display: [Game] [S16LastDay] check cur=3 max=20 played=false");
        log.Feed("[2026.10.31-22.22.47:122][ 51]TLGame: Display: [Game] UIButton@ normal-click Btn_S16Board_ActionCell");
        log.Feed("[2026.10.31-22.22.47:215][ 52]TLLua: Display: [Game] AudioMgr@PlayAudio  AudioEnum  == UI_S16_Food_Target");
        log.Feed("[2026.10.31-22.22.47:249][ 53]TLLua: Display: [Game] AudioMgr@PlayAudio  AudioEnum  == UI_S16_Basic_Quality");
        var run = Assert.Single(log.Runs);
        Assert.Equal(new DateTime(2026, 10, 31, 22, 22, 47, 249, DateTimeKind.Utc).ToLocalTime(), run.ReplacedAt);
        log.Feed("[2026.10.31-22.22.49:029][ 98]TLGame: Display: [Game] UIButton@ normal-click Btn_S16Board_ActionCell");
        log.Feed("[2026.10.31-22.22.49:030][ 98]TLLua: Display: [Game] AudioMgr@PlayAudio  AudioEnum  == UI_S16_Eating_red_screen");
        log.Feed("[2026.10.31-22.22.50:000][120]TLLua: Display: [Game] AudioMgr@PlayAudio  AudioEnum  == UI_S16_Mid_Quality");
        log.Feed("[2026.10.31-22.24.51:131][  1]TLGame: Display: [Game] UIButton@ normal-click Btn_S16Board_ActionCell");
        log.Feed("[2026.10.31-22.24.51:548][  9]TLLua: Display: [Game] AudioMgr@PlayAudio  AudioEnum  == UI_S16_Date_Change");
        log.Feed("[2026.10.31-22.24.52:340][ 40]TLLua: Display: [Game] AudioMgr@PlayAudio  AudioEnum  == UI_S16_Basic_Quality");
        Assert.Equal(new DateTime(2026, 10, 31, 22, 22, 47, 249, DateTimeKind.Utc).ToLocalTime(), run.ReplacedAt);
        Assert.True(run.CardTold);
        // The Hunt card: its click and nothing telling after it (its target pick comes ~2.2 s later).
        log.Feed("[2026.10.31-22.25.10:000][ 60]TLLua: Display: [Game] AudioMgr@PlayAudio  AudioEnum  == UI_S16_Click_Card");
        log.Feed("[2026.10.31-22.25.10:006][ 60]TLGame: Display: [Game] UIButton@ normal-click Btn_S16Board_ActionCell");
        Assert.False(run.CardTold);
        Assert.Equal(new DateTime(2026, 10, 31, 22, 25, 10, 6, DateTimeKind.Utc).ToLocalTime(), run.CardClickedAt);
    }

    [Fact]
    public void The_day_s_move_is_made_from_its_click_to_the_next_day_s_banner()
    {
        // 2026-11-01: the new day's cards show under its banner, before the day counter reaches the log.
        var log = new GameLog();
        log.Feed("[2026.10.31-22.05.51:713][156]TLGame: Display: [Game] UIButton@ normal-click Btn_S16GamePlay_Start");
        log.Feed("[2026.10.31-22.53.35:888][ 12]TLLua: Display: [Game] [S16LastDay] check cur=19 max=20 played=false");
        var run = Assert.Single(log.Runs);
        Assert.False(run.Moved);
        log.Feed("[2026.10.31-22.53.36:729][ 40]TLGame: Display: [Game] UIButton@ normal-click Btn_S16Board_RouteCell");
        Assert.True(run.Moved);
        log.Feed("[2026.10.31-22.53.39:990][ 99]TLLua: Display: [Game] S16BoardNewDayPop Run");
        Assert.False(run.Moved);
        log.Feed("[2026.10.31-22.53.40:774][120]TLLua: Display: [Game] [S16LastDay] check cur=20 max=20 played=false");
        Assert.False(run.Moved);
        Assert.Equal(new DateTime(2026, 10, 31, 22, 53, 39, 990, DateTimeKind.Utc).ToLocalTime(), run.DayStartedAt);
        log.Feed("[2026.10.31-22.53.41:608][140]TLGame: Display: [Game] UIButton@ normal-click Btn_S16Board_RouteCell");
        Assert.True(run.Moved);
    }

    [Fact]
    public void Clicks_pages_and_new_days_are_the_screen_about_to_change()
    {
        Assert.True(GameLogWatcher.IsActivity("[2026.10.31-22.24.54:188][ 61]TLGame: Display: [Game] UIButton@ normal-click Btn_S16Board_RouteCell"));
        Assert.True(GameLogWatcher.IsActivity("[2026.10.31-22.24.52:340][ 40]TLLua: Display: [Game] TipMsgShowMgr@DispatchPageRunChange PageName = S16BoardNewDayPop , PageRunState = Destory "));
        Assert.True(GameLogWatcher.IsActivity("[2026.10.31-22.24.52:340][ 40]TLLua: Display: [Game] [S16LastDay] check cur=20 max=20 played=false"));
        Assert.False(GameLogWatcher.IsActivity("[2026.10.31-22.24.52:340][ 40]TLLua: Display: [Game] AudioMgr@PlayAudio  AudioEnum  == UI_S16_Cell_Light"));
        Assert.False(GameLogWatcher.IsActivity("[2026.10.31-22.24.52:340][ 40]TLGame: Display: [Game] UIButton@ normal-click Btn_Hideout_Stash"));
    }

    [Fact]
    public void The_watcher_reads_a_long_log_as_a_reading_line_by_line_does()
    {
        // The watcher keeps only the lines with "S16", cut out of 1 MB chunks: one cut by the first chunk's end, many lines of
        // the rest of the game between, a run of 20 days.
        var text = new System.Text.StringBuilder();
        string filler = "[2026.11.01-09.00.00:000][  1]LogDataTable: Warning: something of the rest of the game, " + new string('x', 60) + "\r\n";
        void Line(string line) => text.Append(line).Append("\r\n");
        Line("[2026.11.01-09.00.00:000][  1]TLGame: Display: [Game] UIButton@ normal-click Btn_S16GamePlay_Start");
        for (int day = 0; day <= 20; day++)
        {
            // Day 7's line starts 30 characters before the first chunk's end.
            int stop = day == 7 ? (1 << 20) - 30 : text.Length + 100_000;
            while (text.Length + filler.Length <= stop) text.Append(filler);
            if (day == 7) text.Append(new string('y', stop - text.Length - 2)).Append("\r\n");
            Line($"[2026.11.01-09.{day:00}.00:000][  1]TLLua: Display: [Game] [S16LastDay] check cur={day} max=20 played=false");
        }
        string path = Path.Combine(Path.GetTempPath(), $"tartarus-log-{Guid.NewGuid():N}.log");
        File.WriteAllText(path, text.ToString());
        try
        {
            var expected = GameLog.Read(path);
            using var watcher = new GameLogWatcher(path);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(10))
            {
                lock (watcher.Log)
                    if (watcher.Log.Current?.Day == 20) break;
                Thread.Sleep(50);
            }
            lock (watcher.Log)
            {
                Assert.Equal(expected.Runs.Count, watcher.Log.Runs.Count);
                Assert.Equal((expected.Current!.Day, expected.Current.MaxDays, expected.Current.Start), (watcher.Log.Current!.Day, watcher.Log.Current.MaxDays, watcher.Log.Current.Start));
            }
            Assert.True(text.Length > 2 << 20);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
