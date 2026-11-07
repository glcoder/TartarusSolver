namespace Tartarus.Vision;

/// <summary>
/// The hint flow's rules that look only at what they're given (readings, the log's run, times): what a screen asks and when a
/// reading is a passing one. Out of ControlWindow.RequestHint so each has a test (2026-11-07); the flow itself stays there.
/// </summary>
public static class HintRules
{
    /// <summary>How long after a move click, or a new day, the screen may still show what's going away.</summary>
    public static readonly TimeSpan Passing = TimeSpan.FromSeconds(2);

    /// <summary>A Sandbox read whole: the Warden found and no more than a couple of cells under the interface.</summary>
    public static bool WholeBoard(EditableSandbox b) => b.Warden > 0 && b.Cells.Count(c => c.Covered) < 3;

    /// <summary>Another decision than the one hinted on <paramref name="was"/>: the Warden, the Destinations or the Warden's Inmate.</summary>
    /// <remarks>
    /// Only what the new reading shows: Destination cards not read on one frame are no move, nor the phase that goes to
    /// Make Your Decision without them (2026-11-01: a card under the mouse grew and went unread, and the Destination
    /// hint went away before it was clicked). A day's End brings new cards, a move another Warden.
    /// </remarks>
    public static bool MovedOn(EditableSandbox was, EditableSandbox now) =>
        was.Warden != now.Warden
        || now.Destinations.Count > 0 && !was.Destinations.SequenceEqual(now.Destinations)
        || now.Warden > 1 && !now[now.Warden].Covered && SandboxText.CellShort(was[now.Warden]) != SandboxText.CellShort(now[now.Warden]);

    /// <summary>A decision card among the cards read: Consume, Replace, Hunt, Instigate, Bestow or End.</summary>
    public static bool DecisionCard(IEnumerable<string> meanings) =>
        meanings.Any(t => t.Contains("consume") || t.Contains("replace") || t.Contains("hunt") || t.Contains("instigate") || t.Contains("bestow")
            || t == "end" || t.EndsWith(" end"));

    /// <summary>
    /// The day's move clicked (the log tells): Destination cards still on screen are going away, and two quick readings alike
    /// took one left for the only Destination (2026-11-01: "Cell 16" flashed twice after Cell 2 was clicked). Not for long:
    /// cards still there 2 s after the click weren't left (a click the game logged but didn't take).
    /// </summary>
    public static bool DestinationsGoing(LoggedRun? live, DateTime boardAt) =>
        live is { Moved: true } moved && boardAt > moved.MovedAt && boardAt - moved.MovedAt < Passing;

    /// <summary>
    /// A day begins with its move: decision cards on a new day the log saw no move of yet are the last day's going away
    /// (2026-11-01: End hinted for half a second on 11 of 231 new days). Only a log with clicks tells.
    /// </summary>
    public static bool LastDaysCards(LoggedRun? live, DateTime boardAt) =>
        live is { Paged: false, Moved: false } today && today.DayStartedAt > today.MovedAt
        && boardAt > today.DayStartedAt && boardAt - today.DayStartedAt < Passing;
}
