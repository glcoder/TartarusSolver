using System.Diagnostics;
using System.Text.Json;
using Tartarus.Vision.Ocr;

namespace Tartarus.Vision;

/// <summary>
/// The Sandbox scanner without any searching: the screen's layout follows from the frame's size
/// (<see cref="ScreenLayout"/>), OCR reads only the text regions (days, objective, title, cards), and an
/// <see cref="ICellReader"/> reads the cells at their known places. OCR and the cell reader run in parallel.
/// </summary>
public sealed class LayoutScanner : ISandboxScanner
{
    private readonly OcrEngine _ocr;
    private readonly ICellReader _cells;
    private readonly CardIcons? _cardIcons = CardIcons.Load();
    private readonly PickIcons? _instrumentIcons = PickIcons.Instruments(), _treasureIcons = PickIcons.Treasures();
    private ScreenLayout? _layout;

    /// <param name="presence">Tells Sandbox screens from others; without it, a Sandbox is on screen when the day counter reads.</param>
    public LayoutScanner(OcrEngine ocr, ICellReader cells, SandboxPresence? presence = null)
    {
        _ocr = ocr;
        _cells = cells;
        Presence = presence;
    }

    public SandboxPresence? Presence { get; }
    /// <summary>The OCR engine, to set the game's language on (<see cref="OcrEngine.RequestLanguage"/>).</summary>
    public OcrEngine Ocr => _ocr;

    public string Name => "layout+" + _cells.Name;
    public ICellReader CellReader => _cells;

    public ScanResult Scan(Frame frame)
    {
        var sw = Stopwatch.StartNew();
        if (_layout == null || _layout.Width != frame.Width || _layout.Height != frame.Height) _layout = new ScreenLayout(frame.Width, frame.Height);
        _ocr.ApplyLanguage();
        var layout = _layout;

        var reading = Task.Run(() => ReadText(frame, layout));
        ScanResult r;
        var cellsSw = Stopwatch.StartNew();
        try { r = _cells.Read(frame, layout); }
        catch (Exception ex) { r = ScanJson.Failure($"{_cells.Name} failed: {ex.Message}", Name); }
        double cellsMs = cellsSw.Elapsed.TotalMilliseconds;

        ScreenText? text = null;
        try { text = reading.GetAwaiter().GetResult(); }
        catch (Exception ex) { (r.Notes ??= []).Add("OCR failed: " + ex.Message); }

        r.Source = Name;
        r.Layout = layout;
        r.Loc = layout.Sandbox;
        var presenceSw = Stopwatch.StartNew();
        float? presence = Presence?.Score(frame, layout);
        if (presence is float score)
        {
            r.Loc = new SandboxLoc { S = layout.Sandbox.S, Tx = layout.Sandbox.Tx, Ty = layout.Sandbox.Ty, Score = score };
            r.Timings["presence"] = presenceSw.Elapsed.TotalMilliseconds;
        }
        r.Text = text;
        r.Timings["cells"] = cellsMs;
        if (text != null) r.Timings["ocr"] = text.OcrMs;
        if (text != null)
        {
            if (text.Days is int days) r.Days = new DaysRead { Days = days, Conf = text.DaysLine!.Conf };
            var destinations = text.Destinations;
            if (destinations.Count > 0 && destinations.All(d => d != null))
                r.Destinations = new DestinationsRead { Cells = destinations.Select(d => d!.Value - 1).ToList(), Conf = text.Cards.Where(c => c.DestinationCell != null).Min(c => c.Line.Conf) };
            else if (destinations.Count > 0) (r.Notes ??= []).Add("A Destination card's number didn't read.");
        }

        // The layout always "finds" a Sandbox; whether it is on screen is the presence check's (the cell reader
        // finds "inmates" in a pick screen's art). Without a template: the day counter reads.
        r.Ok = r.Cells is { Count: 16 } && (presence is float p ? p >= Presence!.Threshold : text?.Days != null);
        if (!r.Ok)
        {
            var pickSw = Stopwatch.StartNew();
            try
            {
                r.Theme = ReadTheme(frame, layout);
                if (r.Theme == null) r.Pick = ReadPick(frame, layout) ?? ReadTreasure(frame, layout);
            }
            catch (Exception ex) { (r.Notes ??= []).Add("Pick OCR failed: " + ex.Message); }
            r.Timings["pick"] = pickSw.Elapsed.TotalMilliseconds;
            r.Reason ??= r.Theme != null ? "Run theme." : r.Pick?.Kind switch
            {
                PickRead.Instrument => "Select a Torture Instrument.",
                PickRead.Treasure => "Warden's Treasure.",
                _ => "No Tartarus Sandbox on screen.",
            };
        }
        r.Ms = sw.Elapsed.TotalMilliseconds;
        r.TotalMs = r.Ms;
        r.RawJson = JsonSerializer.Serialize(r, VisionJson.Default.ScanResult);
        return r;
    }

    private ScreenText ReadText(Frame frame, ScreenLayout layout)
    {
        var sw = Stopwatch.StartNew();
        var byRole = new Dictionary<string, List<TextLine>>();
        foreach (var (role, rect) in ScreenLayout.TextRegions) byRole[role] = _ocr.Read(frame, layout.ToPixels(rect));
        var text = ScreenText.Parse(byRole, layout, sw.Elapsed.TotalMilliseconds);
        if (text.Days == null && byRole["days"].Count > 0) ReadDaysNumber(frame, layout, text);
        if (_cardIcons != null && text.Cards.Count > 0) text.ApplyCardKinds(layout, cx => _cardIcons.Classify(frame, layout, cx).Kind ?? "cell");
        return text;
    }

    /// <summary>
    /// The day counter's number turns red at 3 days and less, and the detector can miss a red digit on the dark bar
    /// while it finds the words. Look for it by its color instead (any language puts it somewhere in the counter):
    /// the box of the counter's strongly red pixels, read by the recognizer alone.
    /// </summary>
    private void ReadDaysNumber(Frame frame, ScreenLayout layout, ScreenText text)
    {
        var area = layout.ToPixels(ScreenLayout.Days);
        bool bgra = frame.Format == PixelFormat.Bgra32;
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, count = 0;
        for (int y = area.Y0; y < area.Y1; y++)
        {
            int row = y * frame.Stride;
            for (int x = area.X0; x < area.X1; x++)
            {
                int i = row + x * 4;
                int r = frame.Pixels[i + (bgra ? 2 : 0)], g = frame.Pixels[i + 1], b = frame.Pixels[i + (bgra ? 0 : 2)];
                if (r < 110 || r < 2 * g || r < 2 * b) continue;
                count++;
                (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
            }
        }
        int h = y1 - y0 + 1, w = x1 - x0 + 1;
        // A digit or two: about as tall as the counter's letters, not a red tint over the whole bar.
        if (count < 20 || h < area.Height / 4 || w > 3 * h) return;
        int pad = Math.Max(2, h / 4);
        var spot = new PixelBox(x0 - pad, y0 - pad, x1 + pad + 1, y1 + pad + 1).Clamp(frame.Width, frame.Height);
        var res = _ocr.Recognizer.Recognize(frame, [spot])[0];
        // A lone round digit reads as a letter as often as not.
        string read = res.Text.Replace('O', '0').Replace('o', '0').Replace('D', '0').Replace('U', '0').Replace('l', '1').Replace('I', '1');
        if (System.Text.RegularExpressions.Regex.Match(read, @"^\D*(\d{1,2})\D*$") is not { Success: true } m) return;
        text.SetDays(int.Parse(m.Groups[1].Value), new TextLine { Text = read, Box = spot, Ink = spot, Conf = res.Conf, DetScore = 0, Chars = res.Chars });
    }

    /// <summary>Least likeness of a read card name to an instrument's name to take it for that instrument.</summary>
    public const double PickNameScore = 0.75;

    /// <summary>
    /// The Torture Instrument pick, if it is on screen: the names on the three cards, matched to the instruments'
    /// names. Two names that match make it the pick (a card under a tooltip may not read); null otherwise.
    /// </summary>
    private PickRead? ReadPick(Frame frame, ScreenLayout layout)
    {
        var pick = new PickRead { Kind = PickRead.Instrument };
        for (int i = 0; i < ScreenLayout.PickCards; i++)
        {
            pick.Cards.Add(ReadCard(frame, layout, ScreenLayout.PickName(i), i, (i - 1) * ScreenLayout.PickPitch, text =>
            {
                var (instrument, score) = TortureInstruments.Match(text);
                return (instrument.Id, score);
            }));
        }
        if (pick.Cards.Count(c => c.Id != null) < 2) ByIcons(frame, layout, pick, _instrumentIcons, i => (i - 1) * ScreenLayout.PickPitch);
        if (pick.Cards.Count(c => c.Id != null) < 2) return null;
        foreach (var line in _ocr.Read(frame, layout.ToPixels(ScreenLayout.PickRerolls)))
            if (System.Text.RegularExpressions.Regex.Match(line.Text, @"(\d+)\D*$") is { Success: true } m) pick.Rerolls = int.Parse(m.Groups[1].Value);
        return pick;
    }

    /// <summary>The run theme shown before a run, if its scroll is on screen: the theme's id.</summary>
    private string? ReadTheme(Frame frame, ScreenLayout layout)
    {
        foreach (var line in _ocr.Read(frame, layout.ToPixels(ScreenLayout.ThemeName)))
        {
            // The names are short: ask for a closer match than for cards.
            var (theme, score) = RunThemes.Match(line.Text);
            if (score >= 0.8) return theme.Id;
        }
        return null;
    }

    /// <summary>The Warden's Treasure pick, if it is on screen: the four cards' names, as <see cref="ReadPick"/> reads instruments.</summary>
    private PickRead? ReadTreasure(Frame frame, ScreenLayout layout)
    {
        var pick = new PickRead { Kind = PickRead.Treasure };
        for (int i = 0; i < ScreenLayout.TreasureCards; i++)
        {
            pick.Cards.Add(ReadCard(frame, layout, ScreenLayout.TreasureName(i), i, ScreenLayout.TreasureX(i), text =>
            {
                var (treasure, score) = WardenTreasures.Match(text);
                return (treasure.Id, score);
            }));
        }
        if (pick.Cards.Count(c => c.Id != null) < 2) ByIcons(frame, layout, pick, _treasureIcons, ScreenLayout.TreasureX);
        return pick.Cards.Count(c => c.Id != null) >= 2 ? pick : null;
    }

    /// <summary>
    /// The cards whose names didn't match (another language than English, mostly) told by their pictures instead;
    /// <paramref name="cardX"/> gives card i's middle.
    /// </summary>
    private static void ByIcons(Frame frame, ScreenLayout layout, PickRead pick, PickIcons? icons, Func<int, double> cardX)
    {
        if (icons == null) return;
        var found = new List<(PickCardRead Card, string Id, double Score)>();
        foreach (var card in pick.Cards)
        {
            if (card.Id != null) continue;
            var (id, score) = icons.Classify(frame, layout, cardX(card.Index));
            if (id != null) found.Add((card, id, score));
        }
        // A pick offers each at most once: the same picture on two cards is no pick (or the weaker one is wrong).
        foreach (var group in found.GroupBy(f => f.Id))
        {
            var best = group.MaxBy(f => f.Score);
            if (pick.Cards.Any(c => c.Id == best.Id)) continue;
            (best.Card.Id, best.Card.Score) = (best.Id, best.Score);
        }
    }

    /// <summary>
    /// A pick card's name: of the lines read in its name region (a chosen card's name sits higher, and the region
    /// then also holds "Condition" or the like), and of each two lines one under the other (a name set on two lines),
    /// the one most like a known name.
    /// </summary>
    private PickCardRead ReadCard(Frame frame, ScreenLayout layout, CanvasRect region, int index, double x, Func<string, (string Id, double Score)> match)
    {
        var lines = _ocr.Read(frame, layout.ToPixels(region)).OrderBy(l => l.Box.Y0).Select(l => l.Text).ToList();
        var candidates = lines.Concat(lines.Zip(lines.Skip(1), (a, b) => a + " " + b));
        var card = new PickCardRead { Index = index, X = x, Text = string.Join(" ", lines) };
        foreach (string text in candidates)
        {
            var (id, score) = match(text);
            if (score <= card.Score) continue;
            (card.Score, card.Text) = (score, text);
            card.Id = score >= PickNameScore ? id : null;
        }
        return card;
    }

    /// <summary>Disposes the cell reader and the OCR engine: the scanner owns both.</summary>
    public void Dispose()
    {
        _cells.Dispose();
        _ocr.Dispose();
    }
}
