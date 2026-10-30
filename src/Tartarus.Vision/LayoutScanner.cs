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

    // The UI's scale follows from the frame's size, except after the game's resolution changed: it may draw on at the
    // old one's scale (2026-10-29: windowed 1920x1201 to fullscreen 2560x1440, the UI at 1.2 for 1.6, every frame
    // drawn a third too big; a 4K player's frames were off until Alt+Enter). Until a Sandbox or a pick tells the scale,
    // a frame of the new size that isn't the Sandbox at the size's own scale is tried at others.
    private bool _scaleKnown;
    private double? _oldK;
    private DateTime _scaleSearchedAt;
    /// <summary>The least presence that sets the UI's scale: Sandbox screens score 0.27 and up, others 0.06 at most.</summary>
    private const float ScalePresence = 0.25f;
    private static readonly TimeSpan ScaleSearchEvery = TimeSpan.FromSeconds(5);
    /// <summary>The scales tried around a frame size's own, as its fractions.</summary>
    private static readonly double[] ScaleSteps = Enumerable.Range(0, 35).Select(i => 0.5 + 0.03 * i).ToArray();

    public ScanResult Scan(Frame frame)
    {
        var picture = GamePicture.Of(frame);
        var r = ScanPicture(picture);
        if (picture != frame) (r.Notes ??= []).Add($"{GamePicture.Note} {picture.Height} rows tall at the top left of the {frame.Width}x{frame.Height} window, read as {picture.Width}x{picture.Height}.");
        return r;
    }

    private ScanResult ScanPicture(Frame frame)
    {
        if (_layout == null || _layout.Width != frame.Width || _layout.Height != frame.Height)
        {
            _oldK = _layout?.K;
            _layout = new ScreenLayout(frame.Width, frame.Height);
            _scaleKnown = false;
            _scaleSearchedAt = default;
        }
        var r = ScanAt(frame, _layout);
        if ((PickScale(r) ?? SandboxScale(frame, r)) is double k)
        {
            _layout = new ScreenLayout(frame.Width, frame.Height, k);
            _scaleKnown = true;
            var again = ScanAt(frame, _layout);
            (again.Notes ??= []).Add($"The game's UI is drawn at scale {k / ScreenLayout.DefaultK(frame.Width, frame.Height):0.00} of this frame size's.");
            return again;
        }
        if (r.Ok || r.Pick != null) _scaleKnown = true;
        return r;
    }

    /// <summary>
    /// The scale a pick's side cards stand at, when it isn't the layout's: their names' middles measure it (to a few
    /// percent; near the old frame size's scale, that one).
    /// </summary>
    private double? PickScale(ScanResult r)
    {
        if (r.Pick is not { } pick || r.Layout is not { } layout) return null;
        var ks = pick.Cards.Where(c => c.Id != null && c.NameX != null && Math.Abs(c.X) >= 100)
            .Select(c => (c.NameX!.Value - layout.Width / 2.0) / c.X).ToList();
        if (ks.Count == 0) return null;
        double k = ks.Average();
        if (_oldK is double old && Math.Abs(k / old - 1) < 0.06) k = old;
        return Math.Abs(k / layout.K - 1) > 0.05 && k > 0 ? k : null;
    }

    /// <summary>
    /// The scale the Sandbox shows at when it doesn't at the layout's: the presence's best among the old frame size's
    /// scale and a range around this one's; once a scale is known, only the size's own (the game may have caught up:
    /// 2026-10-29, a run's first pick at the size's own scale went unread at the old one's, a pick too then).
    /// Not more often than <see cref="ScaleSearchEvery"/>.
    /// </summary>
    private double? SandboxScale(Frame frame, ScanResult r)
    {
        double own = ScreenLayout.DefaultK(frame.Width, frame.Height);
        if (r.Ok || r.Pick != null || r.Theme != null || _scaleKnown && r.Layout!.K == own
            || DateTime.UtcNow - _scaleSearchedAt < ScaleSearchEvery) return null;
        _scaleSearchedAt = DateTime.UtcNow;
        if (_scaleKnown)
        {
            var ownLayout = new ScreenLayout(frame.Width, frame.Height);
            bool board = Presence is { } presence && presence.Score(frame, ownLayout) >= presence.Threshold;
            return board || (ReadPick(frame, ownLayout) ?? ReadTreasure(frame, ownLayout)) != null ? own : null;
        }
        if (Presence == null) return null;
        var tried = ScaleSteps.Select(f => f * own).Append(_oldK ?? own).Where(k => Math.Abs(k / r.Layout!.K - 1) > 0.02);
        var (best, score) = tried.Select(k => (k, Presence.Score(frame, new ScreenLayout(frame.Width, frame.Height, k)))).MaxBy(t => t.Item2);
        return score >= ScalePresence ? best : null;
    }

    private ScanResult ScanAt(Frame frame, ScreenLayout layout)
    {
        var sw = Stopwatch.StartNew();
        _ocr.ApplyLanguage();

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
        var lines = _ocr.Read(frame, layout.ToPixels(region)).OrderBy(l => l.Box.Y0).ToList();
        var candidates = lines.Select(l => (l.Text, X: l.Box.CenterX))
            .Concat(lines.Zip(lines.Skip(1), (a, b) => (a.Text + " " + b.Text, X: (a.Box.CenterX + b.Box.CenterX) / 2)));
        var card = new PickCardRead { Index = index, X = x, Text = string.Join(" ", lines.Select(l => l.Text)) };
        foreach (var (text, nameX) in candidates)
        {
            var (id, score) = match(text);
            if (score <= card.Score) continue;
            (card.Score, card.Text, card.NameX) = (score, text, nameX);
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
