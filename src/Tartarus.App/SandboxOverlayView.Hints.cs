using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Tartarus.Rules;
using Tartarus.Solver;
using Tartarus.Vision;
using Tartarus.Vision.Ocr;

namespace Tartarus.App;

// The move hints on the overlay, kept to two marks: the best move framed bold green where it is clicked (a decision
// card, a Destination's card, a target's cell, the Cancel button, a pick card), the moves as good as it (within
// Hint.TieWithin; unless the player hides them) framed dashed green. Over each frame, the chests the run is expected to
// end with after the move: each rarity's Riot chest (the game's minimap icons, models/chests) and its expected count;
// and in the debug window a summary line top left.
public sealed partial class SandboxOverlayView
{
    private static readonly Brush HintBrush = Freeze(new SolidColorBrush(Color.FromRgb(90, 235, 125)));
    private static readonly Pen BestPen = FreezePen(new Pen(HintBrush, 5));
    private static readonly Pen TiePen = FreezePen(new Pen(HintBrush, 2) { DashStyle = new DashStyle([4, 3], 0) });
    private static readonly string? ChestDir = NameMatch.FindModelDir("chests", "chromatic.png");

    /// <summary>A place to click and the best of the moves made there.</summary>
    private sealed record Spot(Rect Box, ActionValue Value);

    private static Rect Pixels(ScreenLayout layout, CanvasRect r, double sx, double sy)
    {
        var b = layout.ToPixels(r);
        return new Rect(b.X0 * sx, b.Y0 * sy, b.Width * sx, b.Height * sy);
    }

    /// <summary>
    /// The best spot bold, the ones as good dashed (with <paramref name="alternatives"/>); nothing for the rest. Over each, the chests the run is expected to
    /// end with after that move (Chromatic and Ultimate), "×8.0" by each chest (tenths, rounded): the player
    /// compares them.
    /// </summary>
    private static void DrawSpots(DrawingContext dc, IEnumerable<Spot> spots, double fontSize, double ppd, bool alternatives)
    {
        var ordered = spots.OrderByDescending(s => s.Value.Mean).ToList();
        if (ordered.Count == 0) return;
        double top = ordered[0].Value.Mean;
        for (int i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            bool best = i == 0;
            if (!best && (!alternatives || top - s.Value.Mean >= Hint.TieWithin)) break;
            // Inside the card's or cell's edge (the pen straddles the line): neighbours sit only ~8 canvas units apart.
            var box = Inflate(s.Box, best ? -3 : -1);
            dc.DrawRoundedRectangle(null, best ? BestPen : TiePen, box, 8, 8);
            var parts = new List<(ImageSource? Icon, string Text, Brush Brush)>();
            Chests("chromatic", "Chromatic", s.Value.Chromatic);
            Chests("ultimate", "Ultimate", s.Value.Ultimate);
            if (parts.Count > 0)
            {
                // A size up from the overlay's other captions: the hint is what the player reads at a glance.
                ColorPill(dc, parts, new Point(box.Left + box.Width / 2, box.Top - 2), fontSize * 1.17, ppd);
            }

            // The expected chests of a rarity at the Riot, in tenths, always both: every hint looks alike.
            // The rarity's name stands in when its icon isn't there.
            void Chests(string chest, string name, double expected)
            {
                var icon = Icon(ChestDir, chest);
                parts.Add((icon, (icon == null ? name + " " : "") + "×" + expected.ToString("0.0", CultureInfo.InvariantCulture), HintBrush));
            }
        }
    }

    /// <summary>A pill of pieces, each an optional icon and a colored text, side by side, centred over <paramref name="at"/>.</summary>
    private static void ColorPill(DrawingContext dc, List<(ImageSource? Icon, string Text, Brush Brush)> parts, Point at, double size, double ppd)
    {
        var texts = parts.Select(p => new FormattedText(p.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, p.Brush, ppd)).ToList();
        // The icons' pictures leave a margin inside their squares: the text tucks into it, so each number reads as its
        // icon's; the pieces stand well apart.
        double line = texts.Max(t => t.Height), side = Math.Round(line * 1.5), tuck = side * 0.12, gap = size * 0.84;
        double w = 8 + parts.Select((p, i) => (p.Icon != null ? side - tuck : 0) + texts[i].Width).Sum() + gap * (parts.Count - 1);
        double h = Math.Max(side, line) + 4;
        double x = at.X - w / 2, y = at.Y - h;
        dc.DrawRoundedRectangle(PillBrush, null, new Rect(x, y, w, h), 4, 4);
        x += 4;
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Icon is { } icon)
            {
                dc.DrawImage(icon, new Rect(x, y + (h - side) / 2, side, side));
                x += side - tuck;
            }
            dc.DrawText(texts[i], new Point(x, y + (h - texts[i].Height) / 2));
            x += texts[i].Width + gap;
        }
    }

    /// <summary>
    /// A Sandbox screen's hint. Make Your Decision: each card (Consume and Hunt by their best target); Select a
    /// Destination: its cards; the Consume target pick: the targets' cells, and Cancel worth the best move but
    /// consuming (on a chain, stopping it); the Sewer Ratway: the cells.
    /// </summary>
    private static void DrawSandboxHint(DrawingContext dc, ScreenLayout layout, ScanResult scan, Hint hint, double sx, double sy, double fontSize, double ppd, bool alternatives)
    {
        if (scan.Cells is not { } cells) return;
        var spots = new Dictionary<string, Spot>();
        Rect? CellBox(int n)
        {
            if (cells.FirstOrDefault(c => c.N == n) is not { } c) return null;
            var (x0, y0, x1, y1) = SandboxGeometry.FrameBox(c.X, c.Y, c.K);
            return new Rect(x0 * sx, y0 * sy, (x1 - x0) * sx, (y1 - y0) * sy);
        }
        Rect? CardBox(Func<CardText, bool> which) =>
            scan.Text?.Cards.FirstOrDefault(which) is { } card ? Pixels(layout, CardRect(layout, card), sx, sy) : null;
        Rect? WordCard(string word) => CardBox(t => t.Meaning.Contains(word));
        void Add(string key, Rect? box, ActionValue v)
        {
            if (box is not { } b) return;
            if (!spots.TryGetValue(key, out var s) || v.Mean > s.Value.Mean) spots[key] = new Spot(b, v);
        }

        foreach (var v in hint.Result.Actions)
        {
            var act = v.Action;
            if (hint.ConsumeOnly)
            {
                if (act.Kind is ActionKind.Consume or ActionKind.Chain) Add($"cell{act.Arg}", CellBox(act.Arg), v);
                else Add("cancel", Pixels(layout, ScreenLayout.ConsumeCancel, sx, sy), v);
                continue;
            }
            switch (act.Kind)
            {
                case ActionKind.Move:
                    Add($"move{act.Arg}", CardBox(t => t.DestinationCell == act.Arg + 1) ?? CellBox(act.Arg), v);
                    break;
                case ActionKind.Ratway:
                    Add($"move{act.Arg}", CellBox(act.Arg), v);
                    break;
                case ActionKind.Consume or ActionKind.Chain:
                    Add("consume", WordCard("consume"), v);
                    break;
                case ActionKind.Hunt:
                    Add("hunt", WordCard("hunt"), v);
                    break;
                case ActionKind.Replace:
                    Add("replace", WordCard("replace"), v);
                    break;
                case ActionKind.End or ActionKind.StopChain:
                    Add("end", WordCard("end"), v);
                    break;
                case ActionKind.Interrogate:
                    string word = act.Arg == 0 ? "instigate" : "bestow";
                    Add(word, WordCard(word), v);
                    break;
            }
        }
        DrawSpots(dc, spots.Values, fontSize, ppd, alternatives);
    }

    /// <summary>"Hint: Consume: Cell 9 ≈ Replace (tied) — at the Riot ≈6.4 Chromatic · 2.1 Ultimate · 3.0 Legendary · then End (12,800 playouts)".</summary>
    private static void DrawHintSummary(DrawingContext dc, Hint hint, double ppd)
    {
        // On the Consume target pick the best move but consuming is made by Cancel first: ranked with the targets.
        var shown = hint.Shown;
        if (hint.ConsumeOnly && hint.Result.Actions.Where(a => a.Action.Kind is not (ActionKind.Consume or ActionKind.Chain)).MaxBy(a => a.Mean) is { } stop)
            shown = shown.Append(stop).OrderByDescending(a => a.Mean).ToList();
        if (shown.Count == 0) return;
        string Name(ActionValue a) => !hint.ConsumeOnly || a.Action.Kind is ActionKind.Consume or ActionKind.Chain ? ActionName(a.Action)
            : a.Action.Kind == ActionKind.StopChain ? "Cancel" : $"Cancel → {ActionName(a.Action)}";
        var best = shown[0];
        var ties = shown.Skip(1).Where(a => best.Mean - a.Mean < Hint.TieWithin).ToList();
        var next = shown.Skip(1 + ties.Count).FirstOrDefault();
        string text = $"Hint: {Name(best)}"
            + string.Concat(ties.Select(t => $" ≈ {Name(t)}")) + (ties.Count > 0 ? " (tied)" : "")
            + $" — at the Riot ≈{best.Chromatic:0.0} Chromatic · {best.Ultimate:0.0} Ultimate · {best.Legendary:0.0} Legendary"
            + (next != null ? $" · then {Name(next)}" : "")
            + $"  ({hint.Result.Playouts.ToString("N0", CultureInfo.InvariantCulture)} playouts{(hint.Final ? "" : ", computing…")})";
        Pill(dc, text, new Point(8, 34), HintBrush, 13, ppd);
    }

    /// <summary>Human name of an action, in the game's terms (English: it goes to the log and bug reports too).</summary>
    public static string ActionName(GameAction a) => a.Kind switch
    {
        ActionKind.Move => a.Arg == GameData.Room ? "Interrogation Room" : $"Cell {a.Arg + 1}",
        ActionKind.Consume or ActionKind.Chain => $"Consume: Cell {a.Arg + 1}",
        ActionKind.StopChain => "Cancel",
        ActionKind.Replace => "Replace",
        ActionKind.End => "End",
        ActionKind.Hunt => $"Hunt: Cell {a.Arg + 1}",
        ActionKind.Interrogate => a.Arg == 0 ? "Instigate" : "Bestow",
        ActionKind.PickInstrument => GameData.Info((Instrument)a.Arg).Name,
        ActionKind.Reroll => "Re-roll",
        ActionKind.PickTreasure => GameData.Name((Treasure)a.Arg),
        ActionKind.Ratway => $"Sewer Ratway: Cell {a.Arg + 1}",
        _ => a.ToString(),
    };

    /// <summary>A card's frame, placed by its text (centred on it): two cards sit centred, not on the three-card grid.</summary>
    private static CanvasRect CardRect(ScreenLayout layout, CardText card) =>
        ScreenLayout.CardAt((card.Line.Box.CenterX - layout.Width / 2.0) / layout.K);

    /// <summary>The eye button ringed bold: look at the Sandbox under a pick, or go back to the pick once it's read.</summary>
    private static void DrawEyeHint(DrawingContext dc, ScreenLayout layout, double sx, double sy)
    {
        var eye = Pixels(layout, ScreenLayout.EyeButton, sx, sy);
        dc.DrawEllipse(null, BestPen, new Point(eye.Left + eye.Width / 2, eye.Top + eye.Height / 2), eye.Width / 2, eye.Height / 2);
    }

    /// <summary>An instrument or Warden's Treasure pick: its cards, and the Re-roll button.</summary>
    private static void DrawPickHint(DrawingContext dc, ScreenLayout layout, PickRead pick, Hint hint, double sx, double sy, double ppd, bool alternatives)
    {
        if (hint.Shown.Count == 0) return;
        double fontSize = Math.Clamp(13 * layout.K * sx, 10, 20);
        bool treasure = pick.Kind == PickRead.Treasure;
        var spots = new List<Spot>();
        foreach (var card in pick.Cards)
        {
            if (card.Id == null) continue;
            GameAction? action = treasure
                ? GameObservation.Treasure(card.Id) is { } t ? new GameAction(ActionKind.PickTreasure, (byte)t) : null
                : GameData.InstrumentById(card.Id) is { } i ? new GameAction(ActionKind.PickInstrument, (byte)i) : null;
            if (action is not { } act || hint.Shown.FirstOrDefault(a => a.Action == act) is not { } value) continue;
            spots.Add(new Spot(Pixels(layout, treasure ? ScreenLayout.TreasureCard(card.X) : ScreenLayout.PickCard(card.Index), sx, sy), value));
        }
        if (hint.Shown.FirstOrDefault(a => a.Action.Kind == ActionKind.Reroll) is { } reroll)
            spots.Add(new Spot(Pixels(layout, ScreenLayout.PickRerollButton, sx, sy), reroll));
        DrawSpots(dc, spots, fontSize, ppd, alternatives);
    }
}
