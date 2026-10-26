using System.Text;
using System.Text.RegularExpressions;

namespace Tartarus.Vision.Ocr;

/// <summary>A card under the Sandbox: index 0-2 left to right, its text, and the cell it goes to if it is a Destination.</summary>
public sealed record CardText(int Index, string Text, TextLine Line, int? DestinationCell)
{
    /// <summary>
    /// What the card is by its icon (<see cref="CardIcons"/>): consume, hunt, replace, end, instigate, bestow, room (the
    /// Interrogation Room's Destination) or cell (a Destination to a cell); null when the icons weren't looked at.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>
    /// What the card says, in English whatever the game's language: its kind when known ("consume", "end", "go to
    /// interrogation room"), else its words normalized.
    /// </summary>
    public string Meaning => Kind switch
    {
        null => ScreenText.WordKind(Text) ?? ScreenText.Normalize(Text),
        "cell" => ScreenText.Normalize(Text),
        "room" => "go to interrogation room",
        _ => Kind,
    };
}

/// <summary>
/// What the screen's text says. Each line's meaning comes from the region it was read in (see
/// <see cref="ScreenLayout.TextRegions"/>), not from its words: numbers are read the same in any language, and the
/// cards are told apart by their icons (<see cref="ApplyCardKinds"/>). The objective's name and the titles are still
/// matched as English words.
/// </summary>
public sealed partial class ScreenText
{
    public IReadOnlyDictionary<string, List<TextLine>> ByRole { get; init; } = new Dictionary<string, List<TextLine>>();
    public IEnumerable<TextLine> Lines => ByRole.Values.SelectMany(l => l);

    public int? Days { get; private set; }
    public TextLine? DaysLine { get; private set; }
    /// <summary>The objective's name ("Prison Walk"), from the first line of the objective panel.</summary>
    public string? Objective { get; private set; }
    /// <summary>The progress line(s) under it ("Move 18 tiles 0/18").</summary>
    public string? ObjectiveProgress { get; private set; }
    /// <summary>"5/18" as numbers, when the progress line has them.</summary>
    public (int Done, int Goal)? Progress { get; private set; }
    /// <summary>The phase's title under the Sandbox, as read.</summary>
    public string? Title { get; private set; }
    /// <summary>The Consume target pick is on screen ("Select an Inmate to Consume" over the red screen).</summary>
    public bool ConsumePick { get; private set; }
    /// <summary>The Sewer Ratway's pick: any cell to go to (its title reads "Select an Objective", every cell bracketed).</summary>
    public bool RatwayPick => Title != null && (Normalize(Title).Contains("select an objective")
        || GameText.Likeness(Title, GameText.Interface("Func_S16GamePlay_UnderTitle_Choose")) >= 0.8);
    public List<CardText> Cards { get; } = [];
    /// <summary>Destination cards' cells (1-16, 1 the Interrogation Room), left to right; null for a card whose number didn't read.</summary>
    public List<int?> Destinations => Cards.Where(c => c.Kind is "cell" or "room" || c.Kind == null && (c.DestinationCell != null || IsDestinationText(c.Text))).Select(c => c.DestinationCell).ToList();
    public double OcrMs { get; init; }

    public static ScreenText Parse(IReadOnlyDictionary<string, List<TextLine>> byRole, ScreenLayout layout, double ocrMs = 0)
    {
        var st = new ScreenText { ByRole = byRole, OcrMs = ocrMs };
        if (byRole.TryGetValue("days", out var days))
        {
            // The number of days is the one number on the counter's line, not a letter OCR took for a digit inside a
            // word ("До бунта" reads "o 6yhta").
            // None such (a red number of the last days the detector missed): LayoutScanner looks for it by its color.
            foreach (var line in days)
            {
                if (DayNumberRx().Match(line.Text) is not { Success: true } m) continue;
                st.Days = int.Parse(m.Value);
                st.DaysLine = line;
                break;
            }
        }
        if (byRole.TryGetValue("objective", out var objective) && objective.Count > 0)
        {
            string first = objective[0].Text;
            int colon = first.IndexOfAny([':', '：']);
            st.Objective = (colon >= 0 ? first[(colon + 1)..] : first).Trim();
            if (objective.Count > 1) st.ObjectiveProgress = string.Join(" ", objective.Skip(1).Select(l => l.Text));
            // "5/18" anywhere in the panel: a shorter name in another language shares its line with the count.
            if (ProgressRx().Matches(string.Join(" ", objective.Select(l => l.Text))) is { Count: > 0 } pm)
            {
                var last = pm[^1];
                st.Progress = (int.Parse(last.Groups[1].Value), int.Parse(last.Groups[2].Value));
            }
        }
        if (byRole.TryGetValue("title", out var title) && title.Count > 0) st.Title = string.Join(" ", title.Select(l => l.Text));
        if (byRole.TryGetValue("banner", out var banner))
            st.ConsumePick = banner.Any(l => Normalize(l.Text) is var t && (t.Contains("consume") || t.Contains("select an inmate"))
                || GameText.Likeness(l.Text, GameText.Interface("Func_S16GamePlay_UnderTitle_Eat")) >= 0.75);
        if (byRole.TryGetValue("cards", out var cards))
        {
            foreach (var line in MergeCardLines(cards, layout.K))
            {
                // Which card: by where the line's middle is, in canvas units from the screen's middle.
                double cx = (line.Box.CenterX - layout.Width / 2.0) / layout.K;
                int index = Math.Clamp((int)Math.Round(cx / ScreenLayout.CardPitch) + 1, 0, ScreenLayout.CardCount - 1);
                int? cell = null;
                if (TrailingNumberRx().Match(line.Text) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int n) && n is >= 2 and <= 16) cell = n;
                else if (Normalize(line.Text).Contains("interrogation") || GameText.Likeness(line.Text, GameText.Board(RoomCard)) >= 0.8) cell = 1;
                st.Cards.Add(new CardText(index, line.Text, line, cell));
            }
        }
        return st;
    }

    /// <summary>
    /// A card's text read as two pieces ("Go to" and "Cell 9", a word the detector split) is one line: pieces close
    /// side by side, together no wider than a card.
    /// </summary>
    private static List<TextLine> MergeCardLines(List<TextLine> lines, double k)
    {
        var merged = new List<TextLine>();
        foreach (var line in lines.OrderBy(l => l.Box.X0))
        {
            if (merged.Count > 0 && merged[^1] is var last && (line.Box.X0 - last.Box.X1) / k < 30 && (line.Box.X1 - last.Box.X0) / k < 270)
            {
                var box = new PixelBox(last.Box.X0, Math.Min(last.Box.Y0, line.Box.Y0), Math.Max(last.Box.X1, line.Box.X1), Math.Max(last.Box.Y1, line.Box.Y1));
                var ink = new PixelBox(Math.Min(last.Ink.X0, line.Ink.X0), Math.Min(last.Ink.Y0, line.Ink.Y0), Math.Max(last.Ink.X1, line.Ink.X1), Math.Max(last.Ink.Y1, line.Ink.Y1));
                merged[^1] = new TextLine { Text = last.Text + " " + line.Text, Box = box, Ink = ink, Conf = Math.Min(last.Conf, line.Conf), DetScore = Math.Min(last.DetScore, line.DetScore), Chars = [.. last.Chars, .. line.Chars] };
            }
            else merged.Add(line);
        }
        return merged;
    }

    /// <summary>Where cards' middles can be, in canvas units from the screen's middle: three at 0 and ±a pitch, two at ±half, one at 0.</summary>
    private static readonly double[] CardCentres = [-ScreenLayout.CardPitch, -ScreenLayout.CardPitch / 2, 0, ScreenLayout.CardPitch / 2, ScreenLayout.CardPitch];

    /// <summary>
    /// Each card's kind from its icon, <paramref name="kindAt"/> asked with the card's middle (canvas units from the
    /// screen's middle): a decision card has no cell, the Interrogation Room's card goes to cell 1.
    /// </summary>
    public void ApplyCardKinds(ScreenLayout layout, Func<double, string> kindAt)
    {
        for (int i = 0; i < Cards.Count; i++)
        {
            var card = Cards[i];
            double cx = (card.Line.Box.CenterX - layout.Width / 2.0) / layout.K;
            // The text is centred on its card: the nearest card place, unless the text sits far from any.
            double centre = CardCentres.MinBy(c => Math.Abs(c - cx));
            string kind = kindAt(Math.Abs(centre - cx) < 40 ? centre : cx);
            Cards[i] = card with
            {
                Kind = kind,
                DestinationCell = kind switch { "room" => 1, "cell" => card.DestinationCell, _ => null },
            };
        }
    }

    /// <summary>The day counter's number, read apart from its line (see LayoutScanner's ReadDaysNumber).</summary>
    internal void SetDays(int days, TextLine line)
    {
        Days = days;
        DaysLine = line;
    }

    private static bool IsDestinationText(string text) => Normalize(text) is var t
        && (t.StartsWith("go to", StringComparison.Ordinal) || GameText.Prefixes(CellCard).Any(p => t.StartsWith(p, StringComparison.Ordinal)));

    // The board's words (s16_gameplay_config desc ids): the Destination cards and the decision cards.
    private const int CellCard = 10201, RoomCard = 10203;
    private static readonly (int Id, string Kind)[] DecisionWords =
        [(10301, "consume"), (10302, "replace"), (10303, "hunt"), (10304, "end"), (10305, "instigate"), (10306, "bestow")];

    /// <summary>A decision card's kind by its word in any language of the game ("Пожирание": consume); null when like none.</summary>
    public static string? WordKind(string text)
    {
        string? best = null;
        double score = 0.75;
        foreach (var (id, kind) in DecisionWords)
        {
            double s = GameText.Likeness(text, GameText.Board(id));
            if (s > score) (score, best) = (s, kind);
        }
        return best;
    }

    /// <summary>Lowercase letters, digits and single spaces.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c == ' ' && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Days != null) parts.Add($"days {Days}");
        if (Objective != null) parts.Add($"objective {Objective}" + (Progress is { } p ? $" {p.Done}/{p.Goal}" : ""));
        if (Title != null) parts.Add($"\"{Title}\"");
        if (Cards.Count > 0) parts.Add("cards [" + string.Join(" | ", Cards.Select(c => c.Text + (c.Kind != null ? $" <{c.Kind}{(c.DestinationCell is int n ? " " + n : "")}>" : ""))) + "]");
        return string.Join(" · ", parts);
    }

    // Not straight before a lowercase letter: that digit is a letter of a word ("6yhta"), while the number may run into
    // the next word's capital ("16H" for "16 дн.").
    [GeneratedRegex(@"\d{1,3}(?![a-z])")]
    private static partial Regex DayNumberRx();
    [GeneratedRegex(@"(\d+)\s*/\s*(\d+)")]
    private static partial Regex ProgressRx();
    // Not a digit straight before a Latin letter: a Cyrillic З reads as 3 ("Заменить" as "3aMeHNTb").
    [GeneratedRegex(@"(\d{1,2})(?![A-Za-z])\D*$")]
    private static partial Regex TrailingNumberRx();
}
