using Tartarus.Vision;
using R = Tartarus.Rules;

namespace Tartarus.Rules.Tests;

/// <summary>From a scan's reading to a search's position (HintFlow), with the search, the overlay and the log stood in for.</summary>
public sealed class HintFlowTests
{
    private static readonly DateTime T = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Local);

    private sealed class Sink : IHintSink
    {
        public readonly List<(R.Observation O, bool ConsumeOnly, bool HuntOnly, bool Ahead)> Asked = [];
        public int Cleared, ShownCleared;
        public readonly List<string> Logged = [];
        public bool Request(R.Observation o, bool consumeOnly, bool huntOnly = false, bool ahead = false)
        {
            Asked.Add((o, consumeOnly, huntOnly, ahead));
            return true;
        }
        public void Clear() => Cleared++;
        public void ClearShown() => ShownCleared++;
        public void Log(string message) => Logged.Add(message);
        public void EyeChanged(bool now) { }
        public string BackToPick => "back to the pick";
        public DateTime? HuntShownAt => null;
    }

    private static EditableSandbox Board(int warden, params int[] destinations)
    {
        var b = EditableSandbox.Empty();
        b.Warden = warden;
        b.Days = 12;
        b.Destinations.AddRange(destinations);
        if (destinations.Length > 0) b.Phase = Phases.Destination;
        foreach (var c in b.Cells) c.Kind = CellKind.Unopened;
        return b;
    }

    private static ScanResult Sandbox() => new() { Ok = true, Loc = new SandboxLoc { Score = 0.7 } };

    private static HintInput Seen(EditableSandbox b, int seconds) =>
        new(b, b.Clone(), b.Clone(), T.AddSeconds(seconds), null, null, new RunMemory(), T.AddSeconds(seconds), T.AddSeconds(seconds).ToUniversalTime());

    [Fact]
    public void A_first_reading_is_searched_ahead_and_the_same_again_is_the_hint()
    {
        var sink = new Sink();
        var flow = new HintFlow(sink);
        flow.Take(Sandbox(), Seen(Board(5, 7, 9, 11), 0));
        Assert.True(flow.ConfirmSoon);
        Assert.True(Assert.Single(sink.Asked).Ahead);
        flow.Take(Sandbox(), Seen(Board(5, 7, 9, 11), 1));
        Assert.False(flow.ConfirmSoon);
        Assert.Equal(2, sink.Asked.Count);
        Assert.False(sink.Asked[1].Ahead);
        // The rules' cells are the screen's minus one.
        Assert.Equal([6, 8, 10], sink.Asked[1].O.Destinations);
        Assert.Equal(4, sink.Asked[1].O.Warden);
        Assert.Equal(12, sink.Asked[1].O.RemainingDays);
    }

    [Fact]
    public void Re_rolls_nobody_read_in_this_run_are_none()
    {
        var sink = new Sink();
        new HintFlow(sink).Take(Sandbox(), Seen(Board(5, 7, 9, 11), 0));
        Assert.Equal(0, sink.Asked[0].O.Rerolls);
    }

    [Fact]
    public void A_Sandbox_with_no_Warden_found_is_no_position_and_the_last_hint_stays()
    {
        var sink = new Sink();
        var flow = new HintFlow(sink);
        flow.Take(Sandbox(), Seen(Board(0, 7, 9, 11), 0));
        Assert.Empty(sink.Asked);
        Assert.Equal(0, sink.Cleared);
    }

    [Fact]
    public void A_screen_between_cards_takes_the_hint_away_once_it_is_read_twice()
    {
        var sink = new Sink();
        var flow = new HintFlow(sink);
        // No Destination cards, no decision card: nothing is asked.
        flow.Take(Sandbox(), Seen(Board(5), 0));
        Assert.Equal(0, sink.Cleared);
        flow.Take(Sandbox(), Seen(Board(5), 1));
        Assert.Equal(1, sink.Cleared);
        Assert.Empty(sink.Asked);
    }

    [Fact]
    public void Neither_the_Sandbox_nor_a_pick_clears_the_hint_and_the_eye_button()
    {
        var sink = new Sink();
        var flow = new HintFlow(sink) { EyeHint = true };
        flow.Take(new ScanResult(), Seen(Board(5, 7, 9, 11), 0) with { Sandbox = null });
        Assert.Equal(1, sink.Cleared);
        Assert.False(flow.EyeHint);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void Opulence_s_Treasure_screen_has_the_picks_the_log_says_are_left(int taken, int left)
    {
        var sink = new Sink();
        var flow = new HintFlow(sink);
        var board = Board(5, 7, 9, 11);
        board.Theme = "opulence";
        flow.Take(Sandbox(), Seen(board, 0));
        flow.Take(Sandbox(), Seen(board, 1));
        var run = new LoggedRun { MaxDays = 20, Day = 8, Treasures = 3 + taken, DayStartTreasures = 3 };
        var pick = new ScanResult { Pick = new PickRead { Kind = PickRead.Treasure, Cards = [new PickCardRead { Index = 0, Id = "ring" }, new PickCardRead { Index = 1, Id = "horn" }] } };
        flow.Take(pick, Seen(board, 2) with { Live = run, LogRun = run });
        Assert.Equal(left, sink.Asked[^1].O.TreasurePicks);
    }

    [Fact]
    public void Opulence_s_next_Treasure_screen_asks_for_a_look_at_the_Sandbox_and_hints_after_it()
    {
        var sink = new Sink();
        var flow = new HintFlow(sink);
        var board = Board(5, 7, 9, 11);
        board.Theme = "opulence";
        flow.Take(Sandbox(), Seen(board, 0));
        flow.Take(Sandbox(), Seen(board, 1));
        var pick = new ScanResult { Pick = new PickRead { Kind = PickRead.Treasure, Cards = [new PickCardRead { Index = 0, Id = "ring" }, new PickCardRead { Index = 1, Id = "horn" }] } };
        // The first screen: nothing taken yet, a hint.
        var run = new LoggedRun { MaxDays = 20, Day = 8, PickOpen = true, TreasureOpen = true };
        flow.Take(pick, Seen(board, 2) with { Live = run, LogRun = run });
        Assert.False(flow.EyeHint);
        int asked = sink.Asked.Count;
        // One taken: the second screen is up at once, the Sandbox under it not seen since.
        (run.Treasures, run.PickTakenAt, run.BoardChangedAt) = (1, T.AddSeconds(3), T.AddSeconds(3));
        flow.Take(pick, Seen(board, 4) with { Live = run, LogRun = run });
        Assert.True(flow.EyeHint);
        Assert.Equal(asked, sink.Asked.Count);
        // The eye button: the Sandbox read from the pick, then the pick again.
        run.Looking = true;
        var after = Board(5, 7, 9, 11);
        after.Theme = "opulence";
        after[3].Kind = CellKind.Inmate;
        flow.Take(Sandbox(), Seen(after, 6) with { Live = run, LogRun = run });
        run.Looking = false;
        flow.Take(pick, Seen(after, 7) with { Live = run, LogRun = run });
        Assert.False(flow.EyeHint);
        Assert.Equal(2, sink.Asked[^1].O.TreasurePicks);
        Assert.Equal(R.ObservedKind.Inmate, sink.Asked[^1].O.Cells[2].Kind);
    }

    [Fact]
    public void A_Re_roll_told_by_the_cards_of_a_page_only_log_stays_spent_on_the_run_s_next_pick()
    {
        var sink = new Sink();
        var flow = new HintFlow(sink);
        var run = new LoggedRun { MaxDays = 20, Paged = true, FromStart = true, PickOpen = true };
        ScanResult Offer(int? rerolls, params string[] ids) => new()
        {
            Pick = new PickRead { Kind = PickRead.Instrument, Rerolls = rerolls, Cards = [.. ids.Select((id, k) => new PickCardRead { Index = k, Id = id })] },
        };
        HintInput At(int seconds) => Seen(Board(5, 7, 9, 11), seconds) with { Sandbox = null, Live = run, LogRun = run };
        // The run's first pick with its Re-roll, then other cards on the same pick and no counter: the Re-roll made.
        flow.Take(Offer(1, "shortchain", "scales", "stake"), At(0));
        Assert.Equal(1, sink.Asked[^1].O.Rerolls);
        flow.Take(Offer(null, "casket", "axe", "dogtag"), At(3));
        Assert.Equal(0, sink.Asked[^1].O.Rerolls);
        // The Objective's pick, days later: still none.
        run.InstrumentPicks = 1;
        var board = Board(5, 7, 9, 11);
        board.Instruments = ["casket"];
        flow.Take(Sandbox(), Seen(board, 30) with { Live = run, LogRun = run });
        flow.Take(Sandbox(), Seen(board, 31) with { Live = run, LogRun = run });
        flow.Take(Offer(null, "forged", "scales", "chains"), Seen(board, 33) with { Live = run, LogRun = run });
        Assert.Equal(0, sink.Asked[^1].O.Rerolls);
        Assert.Equal(0, flow.Rerolls);
    }

    [Fact]
    public void A_Warden_s_Treasure_plays_on_the_last_Sandbox_read()
    {
        var sink = new Sink();
        var flow = new HintFlow(sink);
        var board = Board(5, 7, 9, 11);
        flow.Take(Sandbox(), Seen(board, 0));
        flow.Take(Sandbox(), Seen(board, 1));
        var pick = new ScanResult { Pick = new PickRead { Kind = PickRead.Treasure, Cards = [new PickCardRead { Index = 0, Id = "ring" }, new PickCardRead { Index = 1, Id = "vintage" }] } };
        flow.Take(pick, Seen(board, 2));
        var asked = sink.Asked[^1];
        Assert.Equal([R.Treasure.ThumbRing, R.Treasure.CrimsonVintage], asked.O.Treasures);
        Assert.Equal(4, asked.O.Warden);
        Assert.False(asked.Ahead);
    }
}
