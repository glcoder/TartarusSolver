using Tartarus.Vision;

namespace Tartarus.Rules.Tests;

/// <summary>The hint flow's rules on what a screen asks and when a reading is a passing one (HintRules).</summary>
public sealed class HintRulesTests
{
    private static readonly DateTime T = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Local);

    private static EditableSandbox Board(int warden, params int[] destinations)
    {
        var b = EditableSandbox.Empty();
        b.Warden = warden;
        b.Destinations.AddRange(destinations);
        foreach (var c in b.Cells) c.Kind = CellKind.Unopened;
        return b;
    }

    [Fact]
    public void A_board_with_three_covered_cells_or_no_Warden_is_not_whole()
    {
        var b = Board(5);
        Assert.True(HintRules.WholeBoard(b));
        b[2].Covered = b[3].Covered = true;
        Assert.True(HintRules.WholeBoard(b));
        b[4].Covered = true;
        Assert.False(HintRules.WholeBoard(b));
        Assert.False(HintRules.WholeBoard(Board(0)));
    }

    [Fact]
    public void Moved_on_by_another_Warden_other_Destinations_or_another_Inmate_but_not_by_cards_left_unread()
    {
        var was = Board(5, 7, 9);
        Assert.False(HintRules.MovedOn(was, Board(5, 7, 9)));
        // A card under the mouse went unread: no Destinations on this frame is no move.
        Assert.False(HintRules.MovedOn(was, Board(5)));
        Assert.True(HintRules.MovedOn(was, Board(7)));
        Assert.True(HintRules.MovedOn(was, Board(5, 7, 10)));
        var now = Board(5, 7, 9);
        now[5].Kind = CellKind.Inmate;
        now[5].Code = Tartarus.Vision.Code.Lion;
        now[5].Rarity = Tartarus.Vision.Rarity.Ultimate;
        Assert.True(HintRules.MovedOn(was, now));
        // The Warden's cell under the interface: nothing told.
        now[5].Covered = true;
        Assert.False(HintRules.MovedOn(was, now));
    }

    [Theory]
    [InlineData(true, "consume")]
    [InlineData(true, "replace")]
    [InlineData(true, "hunt")]
    [InlineData(true, "end")]
    [InlineData(true, "day end")]
    [InlineData(false, "cell")]
    [InlineData(false, "endure")]
    [InlineData(false)]
    public void Decision_cards_are_told_by_their_meaning(bool decision, params string[] meanings) =>
        Assert.Equal(decision, HintRules.DecisionCard(meanings));

    [Fact]
    public void Destination_cards_are_going_for_two_seconds_after_the_move_click()
    {
        var run = new LoggedRun { DayStartedAt = T, MovedAt = T.AddSeconds(5) };
        Assert.True(HintRules.DestinationsGoing(run, T.AddSeconds(5.5)));
        Assert.False(HintRules.DestinationsGoing(run, T.AddSeconds(7)));
        // A frame from before the click is the day's cards.
        Assert.False(HintRules.DestinationsGoing(run, T.AddSeconds(4)));
        // No move today, or no log.
        Assert.False(HintRules.DestinationsGoing(new LoggedRun { DayStartedAt = T, MovedAt = T.AddSeconds(-30) }, T.AddSeconds(1)));
        Assert.False(HintRules.DestinationsGoing(null, T));
    }

    [Fact]
    public void Decision_cards_on_a_new_day_before_its_move_are_the_last_day_s_for_two_seconds()
    {
        var run = new LoggedRun { MovedAt = T.AddSeconds(-30), DayStartedAt = T };
        Assert.True(HintRules.LastDaysCards(run, T.AddSeconds(1)));
        Assert.False(HintRules.LastDaysCards(run, T.AddSeconds(2)));
        // Moved today already, or a log of pages only (no clicks to tell the move by).
        Assert.False(HintRules.LastDaysCards(new LoggedRun { DayStartedAt = T, MovedAt = T.AddSeconds(0.5) }, T.AddSeconds(1)));
        Assert.False(HintRules.LastDaysCards(new LoggedRun { MovedAt = T.AddSeconds(-30), DayStartedAt = T, Paged = true }, T.AddSeconds(1)));
    }
}
