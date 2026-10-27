using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Tartarus.Vision;

namespace Tartarus.App;

/// <summary>What the overlay shows: the scanned frame's geometry and the (possibly hand-edited) labels.</summary>
public sealed class OverlayModel
{
    public int FrameWidth { get; init; }
    public int FrameHeight { get; init; }
    public ScanResult? Scan { get; init; }
    public EditableSandbox? Sandbox { get; init; }
    public int Selected { get; set; } = -1;
    public bool EditMode { get; set; }
    /// <summary>Frame every interactable region: cells, cards, buttons, Torture Instrument and Chromatic slots, pick cards.</summary>
    public bool ShowRegions { get; set; } = true;
    /// <summary>Draw OCR's regions and the lines it read.</summary>
    public bool ShowText { get; set; }
    /// <summary>Instrument slots shown from memory: their read now isn't an instrument (a tooltip covers them).</summary>
    public IReadOnlySet<int> KeptInstruments { get; init; } = new HashSet<int>();
    /// <summary>The move hint for what is on screen, as far as the search has got.</summary>
    public Hint? Hint { get; init; }
    /// <summary>One line drawn in the top-left corner.</summary>
    public string? Status { get; set; }
    /// <summary>Something the player should do for the hint to hold, drawn large at the top.</summary>
    public string? Warning { get; init; }
    /// <summary>The hint is the eye button: to look at the Sandbox under a pick, or to go back to the pick once it's read.</summary>
    public bool EyeHint { get; init; }
    /// <summary>Only the hint's frames and summary (and warnings): no cells, instruments, Chromatic Inmates or read text.</summary>
    public bool HintsOnly { get; init; }
    /// <summary>The hint's line at the top (the moves, the chests, the playouts): the debug window's, not the player's.</summary>
    public bool HintLine { get; init; }
}

/// <summary>
/// Draws the Sandbox over the game. All geometry is in frame pixels and is scaled to the element's size,
/// so it stays aligned at any DPI as long as the window is sized to the captured window's bounds.
/// </summary>
public sealed partial class SandboxOverlayView : FrameworkElement
{
    private static readonly Brush HitBrush = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
    private static readonly Brush PillBrush = Freeze(new SolidColorBrush(Color.FromArgb(200, 12, 12, 16)));
    private static readonly Brush EditBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 214, 0)));
    private static readonly Brush WardenBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 200, 40)));
    private static readonly Brush DestinationBrush = Freeze(new SolidColorBrush(Color.FromRgb(60, 210, 110)));
    private static readonly Brush IncidentBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 120, 30)));
    private static readonly Brush ConsumableBrush = Freeze(new SolidColorBrush(Color.FromRgb(225, 40, 40)));
    private static readonly Brush UnopenedBrush = Freeze(new SolidColorBrush(Color.FromRgb(140, 140, 150)));
    private static readonly Brush EmptyBrush = Freeze(new SolidColorBrush(Color.FromArgb(150, 200, 200, 200)));
    private static readonly Brush DataBrush = Freeze(new SolidColorBrush(Color.FromRgb(120, 230, 140)));
    private static readonly Brush InstrumentBrush =Freeze(new SolidColorBrush(Color.FromRgb(230, 180, 110)));
    private static readonly Brush TextBrush =Freeze(new SolidColorBrush(Color.FromRgb(70, 220, 230)));
    private static readonly Pen RegionPen = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(150, 70, 220, 230))), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen LinePen = FreezePen(new Pen(TextBrush, 1));
    private static readonly Brush ButtonBrush = Freeze(new SolidColorBrush(Color.FromRgb(120, 200, 255)));
    private static readonly Pen ButtonPen = FreezePen(new Pen(ButtonBrush, 1.5));
    private static readonly Pen EmptySlotPen = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(150, 200, 200, 210))), 1) { DashStyle = DashStyles.Dash });
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Brush[] RarityBrushes =
    [
        Freeze(new SolidColorBrush(Color.FromRgb(225, 225, 225))), // Normal
        Freeze(new SolidColorBrush(Color.FromRgb(80, 150, 255))),  // Magic
        Freeze(new SolidColorBrush(Color.FromRgb(185, 110, 255))), // Rare
        Freeze(new SolidColorBrush(Color.FromRgb(255, 165, 40))),  // Legendary
        Freeze(new SolidColorBrush(Color.FromRgb(255, 70, 70))),   // Ultimate
        Freeze(new SolidColorBrush(Color.FromRgb(255, 110, 210))), // Chromatic
    ];

    private OverlayModel? _model;

    public OverlayModel? Model
    {
        get => _model;
        set
        {
            _model = value;
            InvalidateVisual();
        }
    }

    public static Brush RarityBrush(Rarity rarity) => RarityBrushes[Math.Clamp((int)rarity, 0, RarityBrushes.Length - 1)];

    /// <summary>Frame pixel to element (DIP) scale.</summary>
    private (double X, double Y) Scale(OverlayModel m) =>
        m.FrameWidth > 0 && m.FrameHeight > 0 ? (ActualWidth / m.FrameWidth, ActualHeight / m.FrameHeight) : (1, 1);

    /// <summary>The 0-based cell under an element point, or -1.</summary>
    public int CellAt(Point p)
    {
        var m = _model;
        if (m?.Scan?.Cells is not { } cells) return -1;
        var (sx, sy) = Scale(m);
        double fx = p.X / sx, fy = p.Y / sy;
        foreach (var c in cells)
        {
            var (x0, y0, x1, y1) = SandboxGeometry.FrameBox(c.X, c.Y, c.K);
            if (fx >= x0 && fx <= x1 && fy >= y0 && fy <= y1) return c.N;
        }
        return -1;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var m = _model;
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        if (m?.EditMode == true)
        {
            // Nearly invisible fill so the whole window takes clicks, and a frame that says "editing".
            dc.DrawRectangle(HitBrush, new Pen(EditBrush, 3), full);
        }
        if (m == null) return;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var (sx, sy) = Scale(m);

        if (m.Status is { Length: > 0 } status) Pill(dc, status, new Point(8, 8), Brushes.White, 12, ppd);
        if (m.Warning is { Length: > 0 } warning) Pill(dc, warning, new Point(m.FrameWidth * sx / 2, 64), IncidentBrush, 16, ppd, centerX: true);

        if (m.ShowText && !m.HintsOnly && m.Scan is { } textScan) DrawText(dc, textScan, sx, sy, ppd);
        if (m.EyeHint && m.Scan?.Layout is { } eyeLayout) DrawEyeHint(dc, eyeLayout, sx, sy);
        if (m.Scan is { Pick: { } pick, Layout: { } pickLayout })
        {
            if (!m.HintsOnly) DrawPick(dc, pickLayout, pick, m.ShowRegions, sx, sy, ppd);
            if (m.Hint is { } pickHint) DrawPickHint(dc, pickLayout, pick, pickHint, sx, sy, ppd);
        }
        if (m.HintLine && m.Hint is { } summary && (m.Scan?.Ok == true || m.Scan?.Pick != null)) DrawHintSummary(dc, summary, ppd);
        // The run theme's scroll: say what was read, under the theme's name.
        if (!m.HintsOnly && m.Scan is { Theme: { } runTheme, Layout: { } themeLayout })
        {
            var b = themeLayout.ToPixels(ScreenLayout.ThemeName);
            Pill(dc, $"Run theme: {RunThemes.NameOf(runTheme)} (kept for this run)", new Point((b.X0 + b.X1) / 2.0 * sx, b.Y1 * sy + 4), InstrumentBrush, Math.Clamp(13 * themeLayout.K * sx, 10, 20), ppd, centerX: true);
        }
        if (m.Scan is not { Ok: true, Cells: { } cells } || m.Sandbox is not { } sandbox) return;
        double fontSize = Math.Clamp(11 * sx * (cells.Count > 1 ? cells[1].K : 1) / 0.9, 9, 16);
        // Hints only: the frames on the moves, without the reading of cells, instruments and Chromatic Inmates.
        if (m.HintsOnly)
        {
            if (m.Scan.Layout is { } hintLayout && m.Hint is { } onlyHint) DrawSandboxHint(dc, hintLayout, m.Scan, onlyHint, sx, sy, fontSize, ppd);
            return;
        }
        foreach (var c in cells)
        {
            var label = sandbox.Cells[c.N];
            var (x0, y0, x1, y1) = SandboxGeometry.FrameBox(c.X, c.Y, c.K);
            var box = new Rect(x0 * sx, y0 * sy, (x1 - x0) * sx, (y1 - y0) * sy);
            Brush color = label.Kind switch
            {
                CellKind.Inmate => RarityBrush(label.Rarity),
                CellKind.Unopened => label.VisibleRarity is { } visible ? RarityBrush(visible) : UnopenedBrush,
                CellKind.InterrogationRoom => UnopenedBrush,
                _ => EmptyBrush,
            };
            // A cell under a tooltip shows its last reading, dashed.
            bool unsure = label.Covered || (!label.Edited && c.Conf < 0.5 && label.Kind != CellKind.InterrogationRoom);
            if (m.ShowRegions || label.Edited || c.N == m.Selected)
            {
                var pen = new Pen(label.Edited ? EditBrush : color, c.N == m.Selected ? 3.5 : 1.5);
                if (unsure) pen.DashStyle = DashStyles.Dash;
                dc.DrawRoundedRectangle(null, pen, box, 4, 4);
            }
            if (c.N == m.Selected) dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 1), Inflate(box, 3), 6, 6);

            string? caption = label.Kind switch
            {
                // Two lines: the cells sit close.
                CellKind.Inmate => $"{Codes.Name(label.Code)}\n{label.Rarity}" + (label.Tusk ? "\nTusk" : "") + (label.Prey ? "\nPrey" : ""),
                CellKind.Unopened => "Unopened" + (label.VisibleRarity is { } vr ? $"\n?{vr}" : "") + (label.ScratchMarks ? "\nScratch Marks" : "") + (label.Nest ? "\nRat Nest" : ""),
                CellKind.InterrogationRoom => "Interrogation Room",
                _ => m.EditMode ? "Empty" : null,
            };
            if (caption != null)
            {
                if (label.Covered) caption += "\n(covered)";
                else if (unsure) caption += " ?";
                if (label.Edited) caption = "✎ " + caption;
                Pill(dc, caption, new Point(box.Left + box.Width / 2, box.Top - 2), color, fontSize, ppd, centerX: true, above: true);
            }
            // The red jaws of a Consumable target: top left, where the game draws them over the portrait.
            if (label.Kind == CellKind.Inmate && label.Consumable) Badge(dc, "C", new Point(box.Left, box.Top + box.Height / 3), ConsumableBrush, fontSize, ppd);
            // Right edge, a third down: clear of the caption above the box.
            if (label.Incident) Badge(dc, "!", new Point(box.Right, box.Top + box.Height / 3), IncidentBrush, fontSize, ppd);
            if (sandbox.Warden == c.N + 1) Badge(dc, "W", new Point(box.Left, box.Bottom), WardenBrush, fontSize, ppd);
            int destination = sandbox.Destinations.IndexOf(c.N + 1);
            if (destination >= 0) Badge(dc, $"D{destination + 1}", new Point(box.Right, box.Bottom), DestinationBrush, fontSize, ppd);
        }

        // Sandbox-level reads, just above the Interrogation Room.
        var room = cells[0];
        var (ox0, oy0, _, _) = SandboxGeometry.FrameBox(room.X, room.Y, room.K);
        string header = $"Remaining Days {sandbox.Days?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
            + (sandbox.Theme is { } theme ? $" · {RunThemes.NameOf(theme)}" : "")
            + (m.Scan.Text?.Objective is { } objective ? $" · Objective: {objective}" + (m.Scan.Text.Progress is { } p ? $" {p.Done}/{p.Goal}" : "") : "")
            + (m.Scan.Text?.Title is { } title ? $" · {title}" : "")
            + $" · {m.Scan.TotalMs:0} ms" + (m.Scan.Timings.TryGetValue("ocr", out var ocrMs) ? $" (ocr {ocrMs:0}, cells {m.Scan.Timings.GetValueOrDefault("cells"):0})" : "");
        // Above the Interrogation Room's caption.
        Pill(dc, header, new Point(ox0 * sx, oy0 * sy - 2 * (1.33 * fontSize + 4) - 4), Brushes.White, fontSize, ppd);

        if (m.Scan.Layout is { } layout)
        {
            DrawInstruments(dc, layout, sandbox.Instruments, m.KeptInstruments, m.ShowRegions, sx, sy, fontSize, ppd);
            DrawChromatic(dc, layout, sandbox.Chromatic, m.ShowRegions, sx, sy, fontSize, ppd);
            if (m.ShowRegions) DrawButtons(dc, layout, m.Scan, sx, sy, fontSize, ppd);
            if (m.Hint is { } hint) DrawSandboxHint(dc, layout, m.Scan, hint, sx, sy, fontSize, ppd);
        }
    }

    // The Chromatic Inmates panel: with the regions shown, a frame on each slot (the filled ones solid: they fill in
    // order), and the count under the panel. At the cap of 10 no Inmate can turn Chromatic any more: upgrades stop at Ultimate.
    private static void DrawChromatic(DrawingContext dc, ScreenLayout layout, int count, bool frames, double sx, double sy, double fontSize, double ppd)
    {
        var brush = RarityBrush(Rarity.Chromatic);
        count = Math.Clamp(count, 0, ScreenLayout.ChromaticSlots);
        var pen = new Pen(brush, 1.5);
        for (int i = 0; frames && i < ScreenLayout.ChromaticSlots; i++)
        {
            var b = layout.ToPixels(ScreenLayout.ChromaticSlot(i));
            dc.DrawRoundedRectangle(null, i < count ? pen : EmptySlotPen, new Rect(b.X0 * sx, b.Y0 * sy, b.Width * sx, b.Height * sy), 4, 4);
        }
        // Under the last row, from its left slot.
        var last = layout.ToPixels(ScreenLayout.ChromaticSlot(ScreenLayout.ChromaticSlots - 2));
        string text = $"Chromatic {count}/{ScreenLayout.ChromaticSlots}" + (count >= ScreenLayout.ChromaticSlots ? " (cap)" : "");
        Pill(dc, text, new Point(last.X0 * sx, last.Y1 * sy + 6), brush, fontSize, ppd);
    }

    // The held Torture Instruments: a numbered badge on each slot (with the regions shown, a frame on every slot: an
    // empty one faint, one the slot network doesn't know or shown from memory while a tooltip covers it dashed), and
    // under the slots a list of their icons and names (too long to sit under the slots side by side).
    private static void DrawInstruments(DrawingContext dc, ScreenLayout layout, IReadOnlyList<string> held, IReadOnlySet<int> kept, bool frames, double sx, double sy, double fontSize, double ppd)
    {
        double listTop = 0, listLeft = 0;
        for (int i = 0; i < ScreenLayout.InstrumentSlots; i++)
        {
            var b = layout.ToPixels(ScreenLayout.InstrumentSlot(i));
            var box = new Rect(b.X0 * sx, b.Y0 * sy, b.Width * sx, b.Height * sy);
            if (i >= held.Count)
            {
                if (frames) dc.DrawRoundedRectangle(null, EmptySlotPen, box, 4, 4);
                continue;
            }
            if (frames)
            {
                var pen = new Pen(InstrumentBrush, 1.5);
                if (TortureInstruments.Find(held[i]) == null || kept.Contains(i)) pen.DashStyle = DashStyles.Dash;
                dc.DrawRoundedRectangle(null, pen, box, 4, 4);
            }
            Badge(dc, (i + 1).ToString(CultureInfo.InvariantCulture), new Point(box.Left, box.Top), InstrumentBrush, fontSize, ppd);
            if (i == 0) listLeft = box.Left;
            listTop = Math.Max(listTop, box.Bottom + 6);
        }
        double y = listTop;
        for (int i = 0; i < Math.Min(held.Count, ScreenLayout.InstrumentSlots); i++)
        {
            var instrument = TortureInstruments.Find(held[i]);
            string text = $"{i + 1}. " + (instrument?.Caption ?? "Unknown Torture Instrument") + (kept.Contains(i) ? " (remembered)" : "");
            y += IconPill(dc, text, InstrumentIcon(held[i]), new Point(listLeft, y), InstrumentBrush, fontSize, ppd) + 4;
        }
    }

    // A pick (Torture Instrument or Warden's Treasure): over each card its icon and name (what OCR read when the name
    // didn't match); with the regions shown, a frame on each card (dashed when unmatched) and on the Re-roll button.
    private static void DrawPick(DrawingContext dc, ScreenLayout layout, PickRead pick, bool frames, double sx, double sy, double ppd)
    {
        double fontSize = Math.Clamp(13 * layout.K * sx, 10, 20);
        bool treasure = pick.Kind == PickRead.Treasure;
        var brush = treasure ? RarityBrush(Rarity.Chromatic) : InstrumentBrush;
        foreach (var card in pick.Cards)
        {
            var b = layout.ToPixels(treasure ? ScreenLayout.TreasureCard(card.X) : ScreenLayout.PickCard(card.Index));
            var box = new Rect(b.X0 * sx, b.Y0 * sy, b.Width * sx, b.Height * sy);
            if (frames)
            {
                var pen = new Pen(brush, 2);
                if (card.Id == null) pen.DashStyle = DashStyles.Dash;
                dc.DrawRoundedRectangle(null, pen, box, 6, 6);
            }
            string text = $"{card.Index + 1}. " + (card.Id is { } id ? SandboxText.PickName(pick, id) : $"? {card.Text}");
            var icon = card.Id is { } known ? Icon(treasure ? WardenTreasures.FindIconDir() : TortureInstruments.FindIconDir(), known) : null;
            double h = IconPill(dc, text, icon, new Point(box.Left + box.Width / 2, box.Top - 4), brush, fontSize, ppd, centerX: true, above: true);
            if (!treasure && card.Id is { } wanted && TortureInstruments.WantedForData.Contains(wanted))
                Pill(dc, "wanted for data: the network doesn't know its marks yet", new Point(box.Left + box.Width / 2, box.Top - h - 8), DataBrush, fontSize * 0.85, ppd, centerX: true, above: true);
        }
        if (pick.Rerolls is int rerolls)
        {
            var b = layout.ToPixels(ScreenLayout.PickRerolls);
            Pill(dc, $"Re-roll: {rerolls}", new Point(b.X0 * sx, b.Y0 * sy - 4), InstrumentBrush, fontSize, ppd, above: true);
            if (frames && rerolls > 0)
            {
                var r = layout.ToPixels(ScreenLayout.PickRerollButton);
                dc.DrawRoundedRectangle(null, ButtonPen, new Rect(r.X0 * sx, r.Y0 * sy, r.Width * sx, r.Height * sy), 4, 4);
            }
        }
    }

    // The Sandbox screen's buttons: the cards under the Sandbox (Destinations, decisions), placed by their text, and on
    // the Consume target pick its Cancel button.
    private static void DrawButtons(DrawingContext dc, ScreenLayout layout, ScanResult scan, double sx, double sy, double fontSize, double ppd)
    {
        if (scan.Text is not { } text) return;
        foreach (var card in text.Cards)
        {
            var b = layout.ToPixels(CardRect(layout, card));
            dc.DrawRoundedRectangle(null, ButtonPen, new Rect(b.X0 * sx, b.Y0 * sy, b.Width * sx, b.Height * sy), 6, 6);
        }
        if (text.ConsumePick)
        {
            var b = layout.ToPixels(ScreenLayout.ConsumeCancel);
            var box = new Rect(b.X0 * sx, b.Y0 * sy, b.Width * sx, b.Height * sy);
            dc.DrawRoundedRectangle(null, ButtonPen, box, 6, 6);
            Pill(dc, "Cancel", new Point(box.Left + box.Width / 2, box.Bottom + 2), ButtonBrush, fontSize, ppd, centerX: true);
        }
    }

    private static readonly Dictionary<string, ImageSource?> Icons = [];

    private static ImageSource? InstrumentIcon(string id) => Icon(TortureInstruments.FindIconDir(), id);

    /// <summary>The icon &lt;id&gt;.png from a models folder, or null.</summary>
    private static ImageSource? Icon(string? dir, string id)
    {
        if (dir == null) return null;
        string path = Path.Combine(dir, id + ".png");
        if (Icons.TryGetValue(path, out var icon)) return icon;
        if (File.Exists(path))
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            icon = image;
        }
        return Icons[path] = icon;
    }

    /// <summary>A pill with an icon left of the text; returns its height.</summary>
    private static double IconPill(DrawingContext dc, string text, ImageSource? icon, Point at, Brush fg, double size, double ppd, bool centerX = false, bool above = false)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, fg, ppd);
        double side = Math.Max(ft.Height, 2.4 * size), h = side + 4, w = side + ft.Width + 14;
        at = new Point(centerX ? at.X - w / 2 : at.X, above ? at.Y - h : at.Y);
        var r = new Rect(at.X, at.Y, w, h);
        dc.DrawRoundedRectangle(PillBrush, new Pen(fg, 1), r, 4, 4);
        if (icon != null) dc.DrawImage(icon, new Rect(at.X + 3, at.Y + 2, side, side));
        dc.DrawText(ft, new Point(at.X + side + 9, at.Y + (h - ft.Height) / 2));
        return h;
    }

    // OCR's regions (dashed) and each line it read (its letters' box, and the text under it).
    private static void DrawText(DrawingContext dc, ScanResult scan, double sx, double sy, double ppd)
    {
        if (scan.Layout is { } layout)
            foreach (var (_, rect) in ScreenLayout.TextRegions)
            {
                var b = layout.ToPixels(rect);
                dc.DrawRectangle(null, RegionPen, new Rect(b.X0 * sx, b.Y0 * sy, b.Width * sx, b.Height * sy));
            }
        if (scan.Text is not { } text) return;
        foreach (var line in text.Lines)
        {
            var b = line.Ink;
            var r = new Rect(b.X0 * sx, b.Y0 * sy, b.Width * sx, b.Height * sy);
            dc.DrawRectangle(null, LinePen, r);
            Pill(dc, line.Text, new Point(r.Left, r.Bottom + 1), TextBrush, 10, ppd);
        }
    }

    private static Rect Inflate(Rect r, double d)
    {
        r.Inflate(d, d);
        return r;
    }

    private static void Pill(DrawingContext dc, string text, Point at, Brush fg, double size, double ppd, bool centerX = false, bool above = false)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, fg, ppd);
        double w = ft.Width + 8, h = ft.Height + 2;
        double x = centerX ? at.X - w / 2 : at.X, y = above ? at.Y - h : at.Y;
        dc.DrawRoundedRectangle(PillBrush, null, new Rect(x, y, w, h), 3, 3);
        dc.DrawText(ft, new Point(x + 4, y + 1));
    }

    private static void Badge(DrawingContext dc, string text, Point center, Brush bg, double size, double ppd)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, Brushes.Black, ppd);
        double w = Math.Max(ft.Width + 8, ft.Height + 2), h = ft.Height + 2;
        dc.DrawRoundedRectangle(bg, null, new Rect(center.X - w / 2, center.Y - h / 2, w, h), h / 2, h / 2);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    private static Pen FreezePen(Pen p)
    {
        p.Freeze();
        return p;
    }
}
