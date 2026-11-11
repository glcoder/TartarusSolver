using Tartarus.Vision.Ocr;

namespace Tartarus.Vision;

/// <summary>What the hint flow asks of the app around it: the search, the overlay, the log.</summary>
public interface IHintSink
{
    /// <summary>Searches the position unless it's the one being searched; true when a new search started (the shown hint no longer fits).</summary>
    bool Request(Tartarus.Rules.Observation o, bool consumeOnly, bool huntOnly = false, bool ahead = false);
    /// <summary>The search forgets its position and the shown hint goes.</summary>
    void Clear();
    /// <summary>The shown hint goes; the search stays.</summary>
    void ClearShown();
    void Log(string message);
    /// <summary><see cref="HintFlow.EyeHint"/> changed: the overlay is drawn again, at once or as the reading ends.</summary>
    void EyeChanged(bool now);
    /// <summary>"The Sandbox is read, back to the pick", in the interface's language (for the log).</summary>
    string BackToPick { get; }
    /// <summary>When the hint on screen was shown, if its best move is a Hunt; else null.</summary>
    DateTime? HuntShownAt { get; }
}

/// <summary>What a scan leaves for the hint flow: the window's readings of the Sandbox, the log's run, the run's memory, the time.</summary>
public sealed record HintInput(EditableSandbox? Sandbox, EditableSandbox? SettleRead, EditableSandbox? LastRead, DateTime LastBoardAt,
    LoggedRun? Live, LoggedRun? LogRun, RunMemory Run, DateTime Now, DateTime UtcNow);

/// <summary>
/// From a scan's reading to a search's position, or to no hint: which readings count (settled, not stale, a screen that
/// says what it asks), what the game's log adds to them, what a pick plays on. It was the main window's RequestHint
/// (309 lines among its controls, untestable there); the text is the same but for the names of what comes in
/// (<see cref="HintInput"/>) and goes out (<see cref="IHintSink"/>). One per window; not thread-safe.
/// </summary>
public sealed class HintFlow(IHintSink sink)
{
    private readonly IHintSink _sink = sink;

    // The last Sandbox the hints saw (with the run's memory) and its text: a pick screen's hint plays on from it.
    private EditableSandbox? _hintSandbox;

    private Vision.Ocr.ScreenText? _hintText;

    // The log's run that text was read in.
    private LoggedRun? _hintTextRun;

    // The same scan as read, before Steady: the base of a pick that comes up right after a change.
    private EditableSandbox? _hintRead;

    // When the Sandbox the hints play on (_hintSandbox) was seen: the old one after the log's change leaves it older.
    private DateTime _hintBoardAt;
    // The last reading of the Sandbox looked at from a pick (the eye button): one made after its animations.
    private DateTime _viewedBoardAt;

    // Which Sandbox readings a hint is made of: read alike twice, and not the old Sandbox after the log's change.
    private readonly BoardSettle _boardSettle = new();

    // The reading the hint shown was asked for (its Warden, phase, Destinations, Warden's Inmate).
    private EditableSandbox? _hinted;

    // The Hunt card last offered: where the Warden stood, the day, the Sandbox and the text read with it.
    private (int Warden, int? Days, EditableSandbox Board, Vision.Ocr.ScreenText Text)? _huntOffer;

    // The Warden's Inmate while the Replace card was offered: after the click the card leaves a moment before the new
    // Inmate shows.
    private (int Warden, Tartarus.Vision.Code? Code, Tartarus.Vision.Rarity Rarity, DateTime At)? _replaceOffer;

    // The last counter read: in which run, how many it showed, the Re-rolls the log had seen clicked by then, and the
    // pick it was read on (the log's count of instrument picks, the cards offered).
    private (LoggedRun Run, int Left, int Used, int Pick, string Cards)? _rerollsRead;

    // The last pick whose cards all read: its kind and cards.
    private (string Kind, List<string> Ids)? _lastWholePick;

    // The run's Re-rolls clicked as last seen in the log, and the cards a Re-roll is taking away (until others come).
    private (LoggedRun Run, int Used)? _rerollsSeen;

    private readonly RerolledCards _rerolledCards = new();

    private int? _daysBeforeRed;

    // The last Destinations read (Remaining Days, cells) that held a Prison Incident marker.
    private (int? Days, HashSet<int> Cells)? _markedDestinations;

    // The last reading wasn't settled: the scan loop reads the next frame soon (QuickConfirm), not a period later.
    public bool ConfirmSoon { get; set; }

    // The hint is the eye button: the Sandbox changed under a pick (look at it), or it was read while the pick waits (go back).
    public bool EyeHint { get; set; }

    // Re-rolls the run has left, as the last instrument pick showed them (shared by every pick of the run).
    public int? Rerolls { get; set; }

    // The last Sandbox read before the Consume target pick's red screen.
    public Vision.Ocr.ScreenText? TextBeforeRed { get; set; }

    public const double FadePresence = 0.45;

    public static readonly TimeSpan ChromaticGoing = TimeSpan.FromSeconds(2.5);

    // How long after its reveal's sound a Replace's new Inmate is in its cell on screen.
    public static readonly TimeSpan ReplaceReveal = TimeSpan.FromSeconds(0.4);

    /// <summary>The eye button framed or not: on a pick, look at the Sandbox; on the Sandbox viewed from one, back to it.</summary>
    private void ShowEye(bool on, bool backToPick = false)
    {
        if (on == EyeHint) return;
        EyeHint = on;
        if (on && backToPick) _sink.Log("Hint: " + _sink.BackToPick);
        _sink.EyeChanged(now: false);
    }

    public void Take(ScanResult r, HintInput i)
    {
        // A Sandbox half hidden (a Chromatic's flight or a popup over it, the Warden not found): no position to
        // search, nor a base for the pick that may follow; the last whole one stays.
        // The Sandbox viewed from a pick (the eye button): back to the pick is the hint once a reading of it counts,
        // not as the Sandbox comes up (a quick look went back before it was read, and the pick asked again).
        // A log with clicks says when the eye button was clicked: the pick's own comings and goings reach the log
        // a moment after the screen (2026-10-31: "back to the pick" flashed over the Sandbox as a Warden's Treasure
        // came up and as it was taken).
        bool viewing = i.Live is { PickOpen: true } open && (open.Paged || open.Looking);
        if (r.Ok && i.Sandbox != null && !HintRules.WholeBoard(i.Sandbox))
        {
            if (viewing) ShowEye(false);
            return;
        }
        if (r.Ok && i.Sandbox != null)
        {
            // A Warden's Treasure taken (its Confirm the log's last change, the pick taken then too) may change nothing.
            var changed = i.Live;
            var settle = _boardSettle.Take(i.SettleRead ?? i.Sandbox, i.LastBoardAt, changed?.BoardChangedAt ?? default,
                changed is { } c && c.BoardChangedAt == c.PickTakenAt ? BoardSettle.StaleForTreasure : null);
            // A reading not settled yet: the next frame is read soon, not a whole scan period later.
            ConfirmSoon = settle == BoardSettle.Verdict.Unsettled;
            // A frame caught as a popup comes or goes (presence well below a plain Sandbox's 0.6-0.7) reads wrong where
            // it's dim: not taken unless the next scan reads it alike (2026-10-28: a Warden's Treasure hinted over
            // one at 0.22, two Ultimates read Normal and the Warden elsewhere).
            if (settle == BoardSettle.Verdict.Unsettled && r.Loc is { Score: < FadePresence })
            {
                if (viewing) ShowEye(false);
                return;
            }
            // A pick that covers the Sandbox plays on its latest reading, settled or not: the Warden's Treasure comes up
            // with the Chromatic's sound, before a reading settles (every Treasure asked for the eye button). The old
            // Sandbox still on screen after the log's change counts as seen before it: the pick asks for the eye button.
            _hintSandbox = i.Sandbox.Clone();
            // The day's Destinations with a Prison Incident marker: a move onto one that does the Objective shows the
            // Objective's pick before the incident plays out, and the marker is gone from under the pick.
            if (i.Sandbox.Destinations.Count > 0)
                _markedDestinations = (i.Sandbox.Days, i.Sandbox.Destinations.Where(d => d > 1 && i.Sandbox[d].Incident).ToHashSet());
            // A look at the Sandbox from a pick (the eye button) comes after its animations: always the new one.
            if (settle != BoardSettle.Verdict.Stale || viewing) _hintBoardAt = i.LastBoardAt;
            _hintRead = i.LastRead?.Clone();
            _hintText = r.Text;
            _hintTextRun = i.LogRun;
            // The log knows the day: it stands in when OCR didn't read the counter or read it wrong. The screen
            // shows a new day about a second before the log has it, so one day fewer than the log's is the screen
            // being ahead, not a misread. A pick's hint plays on from this Sandbox too (a popup darkens the counter).
            var live = i.Live;
            if (live is { MaxDays: > 0 } && (_hintSandbox.Days is not int d || (d != live.RemainingDays && d != live.RemainingDays - 1)))
                _hintSandbox.Days = live.RemainingDays;
            // No log's day (none, or a run's length it doesn't tell): the count last read in this run, not none
            // (a red screen or a popup darkens it, and none would be taken for the run's first day).
            _hintSandbox.Days ??= i.Run.LastDays;
            // A run the log saw from its start: its Chromatic count, or the panel's when higher (a sound can go missing).
            if (live is { FromStart: true }) _hintSandbox.Chromatic = Math.Max(_hintSandbox.Chromatic, live.ChromaticAtLeast);
            // The Sandbox viewed from a pick is read now: back to the pick is the hint.
            ShowEye(viewing, backToPick: true);
            if (viewing)
            {
                _viewedBoardAt = i.LastBoardAt;
                _sink.Clear();
                return;
            }
            // A reading not settled yet is searched ahead, its hint held until the next reading confirms it: the hint
            // shown stays meanwhile, unless the position moved on (2026-11-01: "Cell 16" stood over the Warden walking
            // into it, then went to the Consume card). The old Sandbox still on screen after the log's bite, Chromatic
            // or Prison Incident takes the hint away: it's for that one.
            bool ahead = settle == BoardSettle.Verdict.Unsettled;
            switch (settle)
            {
                case BoardSettle.Verdict.Unsettled:
                    if (_hinted is { } was && i.SettleRead is { } now && HintRules.MovedOn(was, now))
                    {
                        _sink.Clear();
                    }
                    break;
                case BoardSettle.Verdict.Stale:
                    _sink.Clear();
                    return;
            }
            // Only a screen that says what it asks: the Consume target pick's banner, Destination cards read, or a
            // decision card. Frames in between (cards sliding in, a day's banner) would be taken for the wrong phase.
            bool destinations = i.Sandbox.Phase == Phases.Destination && i.Sandbox.Destinations.Count > 0;
            // The day's move clicked: Destination cards still on screen are going away.
            if (destinations && HintRules.DestinationsGoing(live, i.LastBoardAt)) destinations = false;
            bool decision = r.Text != null && HintRules.DecisionCard(r.Text.Cards.Select(c => c.Meaning));
            // A new day the log saw no move of yet: decision cards are the last day's going away.
            if (decision && HintRules.LastDaysCards(live, i.LastBoardAt)) decision = false;
            // The target pick: its banner read, or the log's red-screen sound with nothing else on screen (the
            // banner's text doesn't always read).
            bool consumeOnly = r.Text?.ConsumePick == true || live is { ConsumePick: true } && !destinations && !decision;
            // The Hunt card's target pick reads "Select an Objective" like the Sewer Ratway's: after the Hunt card, on
            // the same day and cell, it's the Hunt's. It dims the cells it doesn't offer (an Ultimate read Normal there),
            // so it plays on from the Sandbox and cards read with the Hunt card (2026-10-28: a Sewer Ratway was hinted).
            if (r.Text?.Cards.Any(c => c.Meaning.Contains("hunt")) == true) _huntOffer = (i.Sandbox.Warden, _hintSandbox.Days, _hintSandbox.Clone(), r.Text);
            bool huntPick = r.Text?.RatwayPick == true && _huntOffer is { } hunt && hunt.Warden == i.Sandbox.Warden && hunt.Days == _hintSandbox.Days;
            // The Hunt made: the game starts the next day while the pick's banner still shows; nothing to hint there.
            if (r.Text?.RatwayPick == true && _huntOffer is { } made && made.Warden == i.Sandbox.Warden && made.Days - 1 == _hintSandbox.Days)
            {
                if (!ahead)
                {
                    _sink.Clear();
                }
                return;
            }
            // The Sewer Ratway's pick: any cell to go to.
            if (r.Text?.RatwayPick == true) decision = true;
            // Right after a move the Warden's door is still opening: no decision is readable until an Inmate shows.
            // An End card alone is read all the same: the game left it to click when a merge took the Warden's
            // Inmate away (it ends such a day by itself otherwise).
            var here = i.Sandbox.Warden > 1 ? i.Sandbox[i.Sandbox.Warden] : null;
            bool endOnly = r.Text?.Cards is { Count: > 0 } cards && cards.All(c => c.Meaning is "end" || c.Meaning.EndsWith(" end"));
            if (decision && !consumeOnly && r.Text?.RatwayPick != true && here != null && here.Kind != CellKind.Inmate && !endOnly) decision = false;
            // Replace clicked: its card is gone but the old Inmate is still in the Warden's cell; the day read as
            // after the Replace would be the old Inmate's (an End). Wait for the new one, two seconds at most (it can
            // come out the same); a log with clicks tells when it showed (its reveal's sound), and a frame a moment
            // after that is the new one, alike or not.
            bool replaceCard = r.Text?.Cards.Any(c => c.Meaning.Contains("replace")) == true;
            if (replaceCard && here is { Kind: CellKind.Inmate }) _replaceOffer = (i.Sandbox.Warden, here.Code, here.Rarity, i.Now);
            else if (decision && !consumeOnly && _replaceOffer is { } offer && offer.Warden == i.Sandbox.Warden && here is { Kind: CellKind.Inmate }
                && here.Code == offer.Code && here.Rarity == offer.Rarity && i.Now - offer.At < TimeSpan.FromSeconds(2)
                && !(live is { } replaced && replaced.ReplacedAt > offer.At - TimeSpan.FromSeconds(1) && i.LastBoardAt >= replaced.ReplacedAt + ReplaceReveal))
                decision = false;
            if (!consumeOnly && !destinations && !decision)
            {
                // The Hunt card clicked: its target pick takes ~2.2 s to come, the cards gone meanwhile. The hint stays,
                // its target framed dashed, for the click to come (2026-11-01: it went away for those two seconds).
                bool huntComing = _sink.HuntShownAt is { } huntShown
                    && live is { CardTold: false } clicked && clicked.CardClickedAt > huntShown && i.Now - clicked.CardClickedAt < TimeSpan.FromSeconds(4);
                if (!ahead && !huntComing)
                {
                    _sink.Clear();
                }
                return;
            }
            // A chain's end: the Warden's Inmate reached Chromatic and goes, or has gone, and the red fades before its
            // Treasure comes. Nothing to decide there (2026-11-02: "Cancel, End" hinted for a second at each).
            if (consumeOnly && (here is { Kind: not CellKind.Inmate, Covered: false } || live is { } risen && i.Now - risen.ChromaticAt < ChromaticGoing))
            {
                if (!ahead)
                {
                    _sink.Clear();
                }
                return;
            }
            // The red screen spoils the panels' text too (the Objective): the last text read before it stands in.
            if (!consumeOnly && !huntPick) (TextBeforeRed, _daysBeforeRed) = (r.Text, _hintSandbox.Days);
            else i.Run.ConsumeSeen(_daysBeforeRed);
            if (huntPick) _hintSandbox = _huntOffer!.Value.Board.Clone();
            var text = consumeOnly ? TextBeforeRed ?? r.Text : huntPick ? _huntOffer!.Value.Text : r.Text;
            // Re-rolls nobody read in this run (the app started in its middle) are none: the rules' own default is one.
            var observation = Tartarus.Vision.GameObservation.From(_hintSandbox, text, Rerolls ?? 0);
            i.Run.Objective(observation, settled: !ahead);
            // The log knows whether today's Consume is used, which a missing Consume card doesn't (no target either).
            if (live is { Paged: false, MaxDays: > 0 } && live.RemainingDays == _hintSandbox.Days)
            {
                observation.ConsumedToday = live.ConsumedToday;
                // Consumed today and offered to eat (the red screen, or a Consume card): that's the chain going on.
                bool consumeCard = r.Text?.Cards.Any(c => c.Meaning.Contains("consume")) == true;
                observation.ChainGoesOn = live.ConsumedToday && (consumeOnly || consumeCard);
            }
            // A log of pages only, or none, doesn't tell: the target pick seen on this day does (2026-11-01: a missing
            // card taken for a used Consume made a Replace worth less, and End was hinted on 34 of a player's 79 arrivals).
            else if (live is not { Paged: false } && _hintSandbox.Days is { } dayHere) observation.ConsumedToday = i.Run.ConsumedOn(dayHere);
            if (live is { FromStart: true, ObjectiveDone: true }) observation.ObjectiveDone = true;
            // Survival of the Fittest counts every bite, chain bites too: over the red screen the panel is the one
            // read before it, while the log counts on.
            // Undercurrents the same with the Prison Incidents triggered.
            if (live is { FromStart: true } && observation.Objective is { } objective
                && (objective == Tartarus.Rules.ObjectiveKind.SurvivalOfTheFittest ? live.Consumes
                    : objective == Tartarus.Rules.ObjectiveKind.Undercurrents ? live.Incidents : -1) is int counted and >= 0)
                observation.ObjectiveProgress = Math.Max(observation.ObjectiveProgress, Math.Min(counted, Tartarus.Rules.GameData.Target(objective)));
            if (ahead)
            {
                _sink.Request(observation, consumeOnly, huntPick, ahead: true);
                return;
            }
            _hinted = i.SettleRead?.Clone();
            if (_sink.Request(observation, consumeOnly, huntPick)) _sink.ClearShown();
        }
        else if (r.Pick is { } pick)
        {
            // An instrument pick after the last day (or before any Sandbox) is a new run's first.
            var under = _hintSandbox;
            Vision.Ocr.ScreenText? startText = null;
            if (pick.Kind == PickRead.Instrument && (under == null || under.Days == 0 || i.LogRun?.Day == 0))
            {
                under = EditableSandbox.Empty();
                under.Theme = i.Run.Theme;
                // The Sandbox shows the Objective a moment before the cards cover it: read in this run, it's this run's.
                if (_hintTextRun is { FromStart: true } && _hintTextRun == i.LogRun) startText = _hintText;
            }
            // Re-rolls left: the counter's, else the last one read in this run less those the log saw clicked since
            // (its dim text doesn't read on every frame), else none: a run may have no Re-roll at all (Cage Fight).
            if (pick.Kind == PickRead.Instrument)
            {
                string cards = string.Join(",", pick.Cards.Select(c => c.Id));
                // The last Re-roll takes its counter away, and a log of pages has no clicks: other cards on the same
                // pick are a Re-roll made (2026-10-30, a player's report: Re-roll hinted with none left).
                int Used((LoggedRun Run, int Left, int Used, int Pick, string Cards) seen) =>
                    Math.Max(seen.Run.RerollsUsed - seen.Used,
                        seen.Run.InstrumentPicks == seen.Pick && pick.Cards.Count == 3 && pick.Cards.All(c => c.Id != null) && cards != seen.Cards ? 1 : 0);
                int? left = _rerollsRead is { } seen && seen.Run == i.LogRun ? Math.Max(0, seen.Left - Used(seen)) : null;
                // A counter read after its Re-roll was clicked (the old cards still fading) gives no Re-roll back: a
                // run's only go down (2026-11-01: "Re-roll 1" read again after the click, and the Objective's pick
                // hinted a Re-roll the run no longer had).
                int? read = pick.Rerolls is int counter && left is int leftNow ? Math.Min(counter, leftNow) : pick.Rerolls;
                if (read is int now && i.LogRun != null) _rerollsRead = (i.LogRun, now, i.LogRun.RerollsUsed, i.LogRun.InstrumentPicks, cards);
                Rerolls = read ?? left ?? 0;
            }
            // The Sandbox changed under the pick since it was last seen (a chain, Chromatics leaving it): the hint is
            // for the old one until the player looks at it (the game's eye button) and it's read again.
            // A pick still on screen after the log's Confirm is going away: the change its Confirm made is no reason to
            // look (2026-10-31: every Warden's Treasure taken flashed the eye button).
            bool boardStale = under == _hintSandbox && i.Live is { } logged2 && logged2.BoardChangedAt > _hintBoardAt
                && !(logged2 is { PickOpen: false } && logged2.PickTakenAt >= logged2.BoardChangedAt);
            // Opulence's second and third screen of a Warden's Treasure come up the instant the one before closes (0.0 s
            // apart in the game's log, 2026-11-11): the Sandbox shows only through the fade, 0.4 s after the Confirm, while
            // what the last pick did still plays (the Thumb Ring's second go: one of its three doors read). Only a look at
            // it tells what is there now.
            // shortcut: a log of pages has no Confirm clicks (PickTakenAt), so its clients aren't asked; tell their picks by the pop's closes if Opulence reports from them show the same.
            if (under == _hintSandbox && pick.Kind == PickRead.Treasure && under?.Theme == "opulence"
                && i.Live is { } chain && chain.OpulencePicksLeft < 3 && chain.PickTakenAt > _viewedBoardAt)
                boardStale = true;
            if (boardStale != EyeHint)
            {
                EyeHint = boardStale;
                if (boardStale) _sink.Log("Hint: the Sandbox changed under the pick screen, look at it (the eye button)");
                _sink.EyeChanged(now: true);
            }
            // A card read whole a moment ago that doesn't read now (the one clicked grows and its name shrinks): the
            // hint for the whole pick stays, not one for the cards left (2026-10-30: a weaker instrument hinted for
            // 4 s after the best one was clicked).
            var known = pick.Cards.Where(c => c.Id != null).Select(c => c.Id!).ToList();
            // A Re-roll clicked (the log): the cards on screen go a moment later, and a hint on them would flash
            // (2026-11-01: one of the old three hinted after the click). None until the new offer comes (RerolledCards).
            if (i.LogRun is { } rerun && (_rerollsSeen is not { } seenRun || seenRun.Run != rerun || seenRun.Used != rerun.RerollsUsed))
            {
                if (_rerollsSeen is { } was && was.Run == rerun && rerun.RerollsUsed > was.Used && _lastWholePick is { } before) _rerolledCards.Clicked(before.Ids, i.UtcNow);
                _rerollsSeen = (rerun, rerun.RerollsUsed);
            }
            bool rerolling = _rerolledCards.StillGoing(known, pick.Cards.Count, i.UtcNow);
            bool cardLost = known.Count < pick.Cards.Count && _lastWholePick is { } whole && whole.Kind == pick.Kind && known.All(whole.Ids.Contains);
            if (known.Count == pick.Cards.Count) _lastWholePick = (pick.Kind, known);
            // No pick hint on an old Sandbox: the eye button first.
            if (boardStale || rerolling)
            {
                _sink.Clear();
            }
            else if (cardLost) { }
            else if (under != null)
            {
                var text = under == _hintSandbox ? _hintText : startText;
                // The last scan's own reading of the cells: the change that brought the pick (a door opened on the
                // Objective's Inmate) is in it, not yet in the steadied Sandbox.
                if (under == _hintSandbox && _hintRead != null)
                {
                    under = under.Clone();
                    under.TakeCells(_hintRead);
                }
                // The Chromatic that brought this Warden's Treasure (or one more, gone by unheard): the log counts on
                // while the panel is out of sight.
                if (i.Live is { FromStart: true } counted && counted.ChromaticAtLeast > under.Chromatic)
                {
                    under = under.Clone();
                    under.Chromatic = counted.ChromaticAtLeast;
                }
                // The Objective's pick right on the move that did it, no reading of the landing between (the cards
                // came first): the Sandbox under it is the morning's, Destinations and all. The Warden is on the one
                // that could do the Objective (2026-11-01: the pick was searched with the Warden still at 11 and a
                // move left to make, and its Prison Incident put on cell 11).
                if (pick.Kind == PickRead.Instrument && under.Phase == Phases.Destination && under.Destinations.Count > 0
                    && i.Live is { Moved: true } && LandedFor(under, text) is int landed)
                {
                    under = under.Clone();
                    (under.Warden, under.Phase) = (landed, Phases.Decision);
                    under.Destinations.Clear();
                }
                // Re-rolls left as worked out above: the counter shows a moment after the cards (until then the log
                // tells), and a Warden's Treasure pick has none of its own (the run's go to later offers).
                var observation = Tartarus.Vision.GameObservation.FromPick(pick, under, text);
                // Opulence: three picks of each Warden's Treasure, a screen for each, alike but for the picks left, which
                // only the log tells (2026-11-11: every screen was searched as the first, three picks to come; a run of
                // the user's read 10.6 Chromatic on a Treasure's last screen and 8.9 once it was taken).
                // shortcut: without a log's run every screen still counts as the first; tell the screens apart by the Sandbox seen between them if such clients show in reports.
                if (observation.Treasures.Count > 0 && observation.TreasurePicks > 1 && i.Live is { } picking)
                    observation.TreasurePicks = picking.OpulencePicksLeft;
                // Not the run's first pick: the Objective is this run's.
                if (!observation.StartOfRun) i.Run.Objective(observation);
                observation.Rerolls = Rerolls ?? 0;
                // The Objective done by the move onto a marker: its incident plays out after this pick (the game's
                // order). Undercurrents is only ever done so (2026-11-01: the search took the incident as gone, and
                // the shown chests fell 0.7 on average at that pick).
                observation.IncidentAfterPick = observation.OfferForObjective
                    && (observation.Objective == Tartarus.Rules.ObjectiveKind.Undercurrents
                        || _markedDestinations is { } marked && marked.Days == under.Days && marked.Cells.Contains(under.Warden));
                if (_sink.Request(observation, consumeOnly: false)) _sink.ClearShown();
            }
        }
        else
        {
            _sink.Clear();
            // Neither the Sandbox nor a pick: no eye button to ring either (2026-10-30: rung on in the Hideout for
            // half an hour after the run's last Warden's Treasure).
            ShowEye(false);
        }
    }

    /// <summary>
    /// The one Destination of <paramref name="morning"/> whose move does its Objective: a marked one for Undercurrents,
    /// one far enough for Prison Walk. Null when none or more than one could.
    /// </summary>
    private int? LandedFor(EditableSandbox morning, Vision.Ocr.ScreenText? text)
    {
        var before = Tartarus.Vision.GameObservation.From(morning, text);
        var candidates = before.Objective switch
        {
            Tartarus.Rules.ObjectiveKind.Undercurrents when _markedDestinations is { } marked && marked.Days == morning.Days =>
                morning.Destinations.Where(marked.Cells.Contains).ToList(),
            Tartarus.Rules.ObjectiveKind.PrisonWalk => morning.Destinations
                .Where(d => (d - morning.Warden + 16) % 16 >= Tartarus.Rules.GameData.Target(Tartarus.Rules.ObjectiveKind.PrisonWalk) - before.ObjectiveProgress).ToList(),
            _ => [],
        };
        return candidates.Count == 1 ? candidates[0] : null;
    }
}
