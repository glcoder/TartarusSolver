using Tartarus.Vision;

namespace Tartarus.Rules.Tests;

public class SandboxTests
{
    [Fact]
    public void A_copy_keeps_everything_read()
    {
        // 2026-10-26: the copy lost the run theme, and with it Cage Fight's wider diet from every hint on the Sandbox.
        var b = EditableSandbox.Empty();
        (b.Warden, b.Days, b.Chromatic, b.Theme, b.Objective, b.SandboxEdited) = (4, 12, 3, "cagefight", true, true);
        b.Destinations = [2, 5];
        b.Instruments = ["casket"];
        b.Phase = Phases.Destination;
        b[4].Kind = CellKind.Inmate;
        b[4].Tusk = true;
        var copy = b.Clone();
        Assert.Equal((b.Warden, b.Days, b.Chromatic, b.Theme, b.Objective, b.SandboxEdited, b.Phase), (copy.Warden, copy.Days, copy.Chromatic, copy.Theme, copy.Objective, copy.SandboxEdited, copy.Phase));
        Assert.Equal(b.Destinations, copy.Destinations);
        Assert.Equal(b.Instruments, copy.Instruments);
        Assert.True(copy[4].Tusk);
        Assert.NotSame(b[4], copy[4]);
    }

    [Fact]
    public void A_cell_coming_out_from_cover_takes_its_reading()
    {
        // 2026-10-26: after a Warden's Treasure pick two Lions came out from under the closing animation; the
        // Consumable mark pulsing between the two scans kept them "covered, empty", and the hint took them for Unopened.
        var shown = EditableSandbox.Empty();
        shown[3].Kind = CellKind.Empty;
        shown[3].Covered = true;
        var previousRead = EditableSandbox.Empty();
        previousRead[3].Kind = CellKind.Empty;
        previousRead[3].Covered = true;
        var read = EditableSandbox.Empty();
        (read[3].Kind, read[3].Code, read[3].Rarity) = (CellKind.Inmate, Vision.Code.Lion, Vision.Rarity.Rare);
        read.Steady(shown, previousRead);
        Assert.Equal(CellKind.Inmate, read[3].Kind);
        Assert.False(read[3].Covered);
    }

    [Fact]
    public void The_red_screen_doesn_t_take_an_Ultimate_s_rarity_away()
    {
        // 2026-10-27: under the Consume target pick a Mammoth Ultimate's red frame was lost in the red, it read Normal,
        // and a Rat Rare was hinted to eat it (Cage Fight: lower rarities may be eaten).
        var before = EditableSandbox.Empty();
        before.Warden = 3;
        (before[10].Kind, before[10].Code, before[10].Rarity) = (CellKind.Inmate, Vision.Code.Mammoth, Vision.Rarity.Ultimate);
        (before[3].Kind, before[3].Code, before[3].Rarity) = (CellKind.Inmate, Vision.Code.Rat, Vision.Rarity.Magic);
        var votes = new PickVotes();
        votes.Before(before);
        // The red's first frame, its banner not read yet: taken for an ordinary one.
        var first = before.Clone();
        first[10].Rarity = Vision.Rarity.Normal;
        votes.Before(first);
        var red = first.Clone();
        red[3].Rarity = Vision.Rarity.Rare;
        votes.Apply(red);
        Assert.Equal(Vision.Rarity.Ultimate, red[10].Rarity);
        // The eater's own rise reads as it is.
        Assert.Equal(Vision.Rarity.Rare, red[3].Rarity);
    }

    [Fact]
    public void A_new_Inmate_of_the_same_Code_reads_as_it_is_on_the_red_screen()
    {
        // 2026-10-27: a Lion Ultimate in cell 9 became Chromatic, a Lion Magic came; the red screen showed it Ultimate,
        // and Cancel was hinted there right after Consume on the decision.
        var before = EditableSandbox.Empty();
        before.Warden = 11;
        (before[9].Kind, before[9].Code, before[9].Rarity) = (CellKind.Inmate, Vision.Code.Lion, Vision.Rarity.Ultimate);
        var votes = new PickVotes();
        votes.Before(before);
        var after = before.Clone();
        after[9].Rarity = Vision.Rarity.Magic;
        votes.Before(after);
        votes.Before(after);
        var red = after.Clone();
        votes.Apply(red);
        Assert.Equal(Vision.Rarity.Magic, red[9].Rarity);
    }

    [Fact]
    public void The_pulsing_Consumable_mark_does_not_hold_a_cell_back()
    {
        var shown = EditableSandbox.Empty();
        shown[5].Kind = CellKind.Unopened;
        var previousRead = EditableSandbox.Empty();
        (previousRead[5].Kind, previousRead[5].Code, previousRead[5].Rarity) = (CellKind.Inmate, Vision.Code.Lion, Vision.Rarity.Rare);
        var read = EditableSandbox.Empty();
        (read[5].Kind, read[5].Code, read[5].Rarity, read[5].Consumable) = (CellKind.Inmate, Vision.Code.Lion, Vision.Rarity.Rare, true);
        read.Steady(shown, previousRead);
        Assert.Equal(CellKind.Inmate, read[5].Kind);
    }

    [Fact]
    public void A_hint_takes_a_Sandbox_read_alike_twice()
    {
        var settle = new BoardSettle();
        var t = new DateTime(2026, 10, 27, 22, 12, 57);
        var board = EditableSandbox.Empty();
        board.Warden = 11;
        (board[9].Kind, board[9].Code, board[9].Rarity) = (CellKind.Inmate, Vision.Code.Mammoth, Vision.Rarity.Ultimate);
        Assert.Equal(BoardSettle.Verdict.Unsettled, settle.Take(board, t, default));
        // The pulsing Consumable mark is no other reading.
        board[9].Consumable = true;
        Assert.Equal(BoardSettle.Verdict.Take, settle.Take(board, t.AddSeconds(0.5), default));
        // Mid-flight the Ultimate reads Normal for a frame.
        var flight = board.Clone();
        flight[9].Rarity = Vision.Rarity.Normal;
        Assert.Equal(BoardSettle.Verdict.Unsettled, settle.Take(flight, t.AddSeconds(1), default));
        Assert.Equal(BoardSettle.Verdict.Unsettled, settle.Take(board, t.AddSeconds(1.5), default));
    }

    [Fact]
    public void After_a_bite_the_old_Sandbox_still_on_screen_gets_no_hint()
    {
        // 2026-10-27 22:02:15: Cell 16 eaten, its Inmate stood there 3 s more and was hinted as the chain's next bite.
        var settle = new BoardSettle();
        var t = new DateTime(2026, 10, 27, 22, 2, 13);
        var before = EditableSandbox.Empty();
        before.Warden = 14;
        (before[14].Kind, before[14].Code, before[14].Rarity) = (CellKind.Inmate, Vision.Code.Lion, Vision.Rarity.Rare);
        (before[16].Kind, before[16].Code, before[16].Rarity) = (CellKind.Inmate, Vision.Code.Wolf, Vision.Rarity.Magic);
        settle.Take(before, t, default);
        Assert.Equal(BoardSettle.Verdict.Take, settle.Take(before, t.AddSeconds(1), default));
        var bite = t.AddSeconds(1.5);
        Assert.Equal(BoardSettle.Verdict.Stale, settle.Take(before, t.AddSeconds(2), bite));
        Assert.Equal(BoardSettle.Verdict.Stale, settle.Take(before, t.AddSeconds(4), bite));
        var after = before.Clone();
        after[16].Kind = CellKind.Unopened;
        after[14].Rarity = Vision.Rarity.Legendary;
        Assert.Equal(BoardSettle.Verdict.Unsettled, settle.Take(after, t.AddSeconds(4.5), bite));
        Assert.Equal(BoardSettle.Verdict.Take, settle.Take(after, t.AddSeconds(5), bite));
    }

    [Fact]
    public void A_Sandbox_the_log_s_change_left_as_it_was_is_hinted_again_after_a_while()
    {
        var settle = new BoardSettle();
        var t = new DateTime(2026, 10, 27, 22, 0, 0);
        var board = EditableSandbox.Empty();
        board.Warden = 5;
        settle.Take(board, t, default);
        var incident = t.AddSeconds(0.5);
        Assert.Equal(BoardSettle.Verdict.Stale, settle.Take(board, t.AddSeconds(1), incident));
        Assert.Equal(BoardSettle.Verdict.Take, settle.Take(board, incident + BoardSettle.StaleFor, incident));
    }
}
