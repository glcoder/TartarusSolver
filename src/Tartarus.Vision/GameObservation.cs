using Tartarus.Vision.Ocr;
using R = Tartarus.Rules;

namespace Tartarus.Vision;

/// <summary>
/// The scanner's reading of the screen as the rules' <see cref="R.Observation"/>: what the move hints' search starts
/// from. Cells: screen cell n is the rules' cell n-1.
/// </summary>
public static class GameObservation
{
    public static R.Observation From(EditableSandbox b, ScreenText? text = null, int? rerolls = null)
    {
        var o = new R.Observation
        {
            Warden = b.Warden > 0 ? b.Warden - 1 : 0,
            RemainingDays = b.Days,
            Chromatic = b.Chromatic,
            Theme = Theme(b.Theme),
            Rerolls = rerolls,
        };
        foreach (var id in b.Instruments)
            if (R.GameData.InstrumentById(id) is { } i) o.Instruments.Add(i);
        for (int n = 2; n <= 16; n++)
        {
            var c = b[n];
            o.Cells[n - 1] = new R.ObservedCell
            {
                Kind = c.Kind switch { CellKind.Inmate => R.ObservedKind.Inmate, CellKind.Empty => R.ObservedKind.Empty, _ => R.ObservedKind.Unopened },
                Code = Code(c.Code),
                Rarity = (R.Rarity)Math.Min((int)c.Rarity, (int)R.Rarity.Ultimate),
                Shown = c.Kind == CellKind.Unopened && c.VisibleRarity is { } v ? (R.Rarity)v : null,
                Scratch = c.ScratchMarks,
                Nest = c.Nest,
                Tusk = c.Tusk,
                Prey = c.Prey,
                Marker = c.Incident,
            };
        }
        if (b.Phase == Phases.Destination)
            foreach (int d in b.Destinations) o.Destinations.Add(d - 1);
        else if (text != null)
        {
            // Make Your Decision's cards say what is left today: only End once the decision is taken; Instigate and
            // Bestow in the Interrogation Room.
            var cards = text.Cards.Select(c => c.Meaning).ToList();
            // A card missing from a decision row: that one is used today (a Consume or Hunt, a Replace), or isn't possible.
            bool replace = cards.Any(c => c.Contains("replace"));
            bool consume = cards.Any(c => c.Contains("consume") || c.Contains("hunt"));
            bool end = cards.Any(c => c == "end" || c.EndsWith(" end") || c.Contains("end turn"));
            o.Interrogation = cards.Any(c => c.Contains("instigate") || c.Contains("bestow"));
            o.Ratway = text.RatwayPick;
            if (!o.Interrogation && (replace || consume || end))
            {
                o.ReplaceUsed = !replace;
                o.DecisionUsed = !consume;
            }
        }
        if (text != null && Objective(text) is { } objective)
        {
            o.Objective = objective;
            o.ObjectiveDone = text.ObjectiveProgress?.Contains("Complete", StringComparison.OrdinalIgnoreCase) == true;
            if (text.Progress is { } p) o.ObjectiveProgress = p.Done;
        }
        return o;
    }

    /// <summary>
    /// A pick screen over the last Sandbox read (<paramref name="under"/>, the run's memory of it): the cards offered
    /// become the choice waiting. The run's first offer comes before the day's move.
    /// </summary>
    public static R.Observation FromPick(PickRead pick, EditableSandbox under, ScreenText? lastText = null)
    {
        // No "Remaining attempts" on the pick: no Re-roll left (after one, the button is gone).
        var o = From(under, lastText, pick.Kind == PickRead.Instrument ? pick.Rerolls ?? 0 : null);
        if (pick.Kind == PickRead.Treasure)
        {
            foreach (var c in pick.Cards)
                if (Treasure(c.Id) is { } t) o.Treasures.Add(t);
            if (o.Theme == R.Theme.Opulence) o.TreasurePicks = 3;
            return o;
        }
        foreach (var c in pick.Cards)
            if (c.Id != null && R.GameData.InstrumentById(c.Id) is { } i) o.Offer.Add(i);
        o.OfferForObjective = o.Instruments.Count > 0;
        // No instrument held yet: the run's first pick, before the Sandbox under the cards was ever seen.
        o.StartOfRun = !o.OfferForObjective;
        return o;
    }

    public static R.Treasure? Treasure(string? id) => id switch
    {
        "ring" => R.Treasure.ThumbRing,
        "horn" => R.Treasure.BrassHorn,
        "vintage" => R.Treasure.CrimsonVintage,
        "sin" => R.Treasure.BrandOfSin,
        _ => null,
    };

    /// <summary>
    /// The Objective from the panel's lines (the name may wrap: "Survival of the" / "Fittest"; the task line helps), or,
    /// in another language, from its goal: each Objective counts to its own number.
    /// </summary>
    public static R.ObjectiveKind? Objective(ScreenText text)
    {
        string all = ScreenText.Normalize((text.Objective ?? "") + " " + (text.ObjectiveProgress ?? ""));
        if (all.Contains("king of") || all.Contains("reaches ultimate")) return R.ObjectiveKind.KingOfTheCaged;
        if (all.Contains("survival") || all.Contains("consume 4")) return R.ObjectiveKind.SurvivalOfTheFittest;
        if (all.Contains("behind cell") || all.Contains("open 10")) return R.ObjectiveKind.BehindCellDoors;
        if (all.Contains("undercurrent") || all.Contains("prison incident")) return R.ObjectiveKind.Undercurrents;
        if (all.Contains("prison walk") || all.Contains("move 18")) return R.ObjectiveKind.PrisonWalk;
        // The name in another language: the game's names have "Objective: " (or its translation) before it, as the panel.
        if (text.Objective is { Length: > 2 } read)
        {
            var names = GameText.Names("objectives").Select(n => (n.Id, Name: n.Text[(n.Text.IndexOfAny([':', '：']) + 1)..].Trim())).ToList();
            if (names.Count > 0 && NameMatch.Best(read, names, n => n.Name) is ({ } best, >= 0.75) && Enum.TryParse<R.ObjectiveKind>(best.Id, out var kind))
                return kind;
        }
        return text.Progress?.Goal switch
        {
            1 => R.ObjectiveKind.KingOfTheCaged,
            4 => R.ObjectiveKind.SurvivalOfTheFittest,
            10 => R.ObjectiveKind.BehindCellDoors,
            2 => R.ObjectiveKind.Undercurrents,
            18 => R.ObjectiveKind.PrisonWalk,
            _ => null,
        };
    }

    public static R.Code Code(Code? code) => code switch
    {
        Vision.Code.Mammoth => R.Code.Mammoth,
        Vision.Code.Lion => R.Code.Lion,
        Vision.Code.Wolf => R.Code.Wolf,
        _ => R.Code.Rat,
    };

    public static R.Theme Theme(string? id) => id switch
    {
        "tyranny" => R.Theme.Tyranny,
        "opulence" => R.Theme.Opulence,
        "ironlaw" => R.Theme.IronLaw,
        "cagefight" => R.Theme.CageFight,
        _ => R.Theme.None,
    };
}
