using Tartarus.Vision.Ocr;

namespace Tartarus.Vision;

/// <summary>Which point of the window a layout element is placed from.</summary>
public enum Anchor { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

/// <summary>A rectangle in canvas units (the 1600x900 layout), as an offset from an anchor of the window.</summary>
public readonly record struct CanvasRect(Anchor Anchor, double X0, double Y0, double X1, double Y1)
{
    public CanvasRect Offset(double dx, double dy) => this with { X0 = X0 + dx, X1 = X1 + dx, Y0 = Y0 + dy, Y1 = Y1 + dy };
}

/// <summary>
/// Where everything is on the Tartarus screen, from the window's client size alone. The screen is laid out
/// on a 1600x900 canvas scaled by K = min(W, H * 16/9) / 1600 (the game's UI scale setting doesn't apply to
/// it); each element keeps a fixed offset in canvas units from an edge or the middle of the window. The
/// Sandbox is in the middle. Measured on 1600x900, 1680x1050, 1600x1200, 1920x1200, 2560x1440 and 1930x835.
/// </summary>
public sealed class ScreenLayout
{
    public const double CanvasWidth = 1600, CanvasHeight = 900;

    // The Sandbox in canvas units: js/scan.js's reference homography H0 scaled and shifted (measured).
    private const double SandboxScale = 1.21194, SandboxX = 2.44, SandboxY = -0.06;

    public ScreenLayout(int width, int height)
    {
        Width = width;
        Height = height;
        K = Math.Min(width, height * 16.0 / 9.0) / CanvasWidth;
        Sandbox = new SandboxLoc
        {
            S = SandboxScale * K,
            Tx = width / 2.0 + (SandboxX - CanvasWidth / 2) * K,
            Ty = height / 2.0 + (SandboxY - CanvasHeight / 2) * K,
            Score = 1,
        };
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>Pixels per canvas unit.</summary>
    public double K { get; }
    /// <summary>The Sandbox's placement for <see cref="SandboxGeometry"/>.</summary>
    public SandboxLoc Sandbox { get; }

    public (double X, double Y) ToPixels(Anchor anchor, double x, double y)
    {
        double ax = anchor switch
        {
            Anchor.TopLeft or Anchor.Left or Anchor.BottomLeft => 0,
            Anchor.Top or Anchor.Center or Anchor.Bottom => Width / 2.0,
            _ => Width,
        };
        double ay = anchor switch
        {
            Anchor.TopLeft or Anchor.Top or Anchor.TopRight => 0,
            Anchor.Left or Anchor.Center or Anchor.Right => Height / 2.0,
            _ => Height,
        };
        return (ax + x * K, ay + y * K);
    }

    public PixelBox ToPixels(CanvasRect r)
    {
        var (x0, y0) = ToPixels(r.Anchor, r.X0, r.Y0);
        var (x1, y1) = ToPixels(r.Anchor, r.X1, r.Y1);
        return new PixelBox((int)Math.Floor(x0), (int)Math.Floor(y0), (int)Math.Ceiling(x1), (int)Math.Ceiling(y1)).Clamp(Width, Height);
    }

    /// <summary>Padlock point and scale of cell n (1-16), as <see cref="SandboxGeometry.At"/> gives them.</summary>
    public (double X, double Y, double K) Cell(int n)
    {
        var (u, v) = SandboxGeometry.Ring[n - 1];
        var (x, y) = SandboxGeometry.At(Sandbox, u, v);
        return (x, y, SandboxGeometry.ScaleAt(Sandbox, u, v));
    }

    /// <summary>A region of the frame resampled to its size in canvas units: the same picture at any resolution.</summary>
    public Frame Crop(Frame frame, CanvasRect r)
    {
        var (x0, y0) = ToPixels(r.Anchor, r.X0, r.Y0);
        var (x1, y1) = ToPixels(r.Anchor, r.X1, r.Y1);
        return CellCrops.CropBox(frame, x0, y0, x1, y1, (int)Math.Round(r.X1 - r.X0), (int)Math.Round(r.Y1 - r.Y0));
    }

    // ---------- Regions, in canvas units ----------

    /// <summary>"19 day(s) until Riot" at the top middle (wide enough for longer languages).</summary>
    public static readonly CanvasRect Days = new(Anchor.Top, -330, 2, 330, 52);
    /// <summary>"Objective: ..." and its progress line(s), top left.</summary>
    public static readonly CanvasRect Objective = new(Anchor.TopLeft, 55, 105, 470, 232);
    /// <summary>The phase's title under the Sandbox ("Select a Destination", "Make Your Decision", ...).</summary>
    public static readonly CanvasRect Title = new(Anchor.Center, -270, 116, 270, 150);
    // A card under the mouse pointer rises by 7.5 canvas units (9 px at 1920x1200), the others stay: card
    // regions reach that much higher than a resting card.
    public const double CardHoverRise = 7.5;
    /// <summary>The row of cards under the Sandbox (Destinations, Decisions).</summary>
    public static readonly CanvasRect CardRow = new(Anchor.Center, -432, 168 - CardHoverRise, 432, 324);
    /// <summary>The cards' text line ("Go to Cell 4", "Consume", ...), resting or raised.</summary>
    public static readonly CanvasRect CardText = new(Anchor.Center, -432, 262 - CardHoverRise - 2, 432, 310);
    public const double CardPitch = 288;
    public const int CardCount = 3;

    /// <summary>
    /// The card whose middle is <paramref name="x"/> canvas units right of the screen's middle: fewer than three cards
    /// sit centred (two at ±half a pitch), so a card is placed by its text, which is centred on it.
    /// </summary>
    public static CanvasRect CardAt(double x) => new(Anchor.Center, x - 140, 168 - CardHoverRise, x + 140, 324);

    /// <summary>Card i (0-2, left to right) of the row under the Sandbox, with room above for it rising under the pointer.</summary>
    public static CanvasRect Card(int i) => new(Anchor.Center, (i - 1) * CardPitch - 140, 168 - CardHoverRise, (i - 1) * CardPitch + 140, 324);

    /// <summary>Torture Instrument slot i (0 = first) under the "Torture Instrument" label, top left.</summary>
    /// The icon (~75 units on a side) is centred in it: fitted with the game's icons on slot crops.
    public static CanvasRect InstrumentSlot(int i) => new(Anchor.TopLeft, 71.5 + 72.7 * i, 274, 134.5 + 72.7 * i, 378);
    /// <summary>A run holds at most 2 Torture Instruments: one picked at the start, one for completing the Objective.</summary>
    public const int InstrumentSlots = 2;

    /// <summary>Chromatic Prisoner slot i (0-9: row by row, left column first), top right.</summary>
    public static CanvasRect ChromaticSlot(int i)
    {
        double cx = i % 2 == 0 ? -168.3 : -95.4, cy = 192.5 + 83.25 * (i / 2);
        return new CanvasRect(Anchor.TopRight, cx - 30, cy - 40, cx + 30, cy + 40);
    }
    public const int ChromaticSlots = 10;

    // The Torture Instrument pick ("Select a Torture Instrument", at the run's start and for the Objective): three
    // cards in the middle (measured at 1920x1200). A card under the pointer rises ~5 units; the chosen one grows and
    // its name rises ~40: the name regions reach that high (other text there is told apart by matching the names).
    public const int PickCards = 3;
    public const double PickPitch = 400;
    /// <summary>Pick card i (0-2, left to right): its frame.</summary>
    public static CanvasRect PickCard(int i) => new(Anchor.Center, (i - 1) * PickPitch - 190, -306, (i - 1) * PickPitch + 190, 275);
    /// <summary>The instrument's name on pick card i, under its icon (one line; long names are set smaller), resting or chosen.</summary>
    public static CanvasRect PickName(int i) => new(Anchor.Center, (i - 1) * PickPitch - 178, -98, (i - 1) * PickPitch + 178, -8);
    /// <summary>The Re-roll button under "Remaining attempts N" (measured at 1920x1200).</summary>
    public static readonly CanvasRect PickRerollButton = new(Anchor.Center, -255, 372, -32, 428);
    /// <summary>"Remaining attempts N" over the Re-roll button, left of Confirm; only there while a re-roll is left.</summary>
    public static readonly CanvasRect PickRerolls = new(Anchor.Center, -330, 335, 40, 374);

    // The Warden's Treasure pick after a Chromatic ("Prisoner rarity reached Chromatic. Warden's Treasure obtained."):
    // four cards in the middle (measured at 1920x1200); the chosen one gets a dark square frame, its name stays.
    public const int TreasureCards = 4;
    public const double TreasurePitch = 365;
    /// <summary>The middle of treasure card i (0-3), in canvas units right of the screen's middle.</summary>
    public static double TreasureX(int i) => (i - 1.5) * TreasurePitch;
    /// <summary>The frame of the treasure card whose middle is <paramref name="x"/> canvas units right of the screen's middle.</summary>
    public static CanvasRect TreasureCard(double x) => new(Anchor.Center, x - 175, -300, x + 175, 275);
    /// <summary>The treasure's name on card i, under its picture.</summary>
    public static CanvasRect TreasureName(int i) => new(Anchor.Center, TreasureX(i) - 170, -92, TreasureX(i) + 170, 0);
    /// <summary>The treasure's picture on card i.</summary>
    public static CanvasRect TreasureIcon(int i) => new(Anchor.Center, TreasureX(i) - 80, -232, TreasureX(i) + 80, -72);

    /// <summary>
    /// The run theme's name on the scroll that shows it before a run ("Tyranny" over the theme's effect; measured at
    /// 1920x1200, in the lobby).
    /// </summary>
    public static readonly CanvasRect ThemeName = new(Anchor.Center, -240, -186, 240, -136);

    /// <summary>The regions OCR reads, by role.</summary>
    /// <summary>
    /// The Consume target pick's banner ("Select an Inmate to Consume") over the red screen, below the Sandbox; and its
    /// Cancel button, right of the middle (measured at 1920x1200).
    /// </summary>
    public static readonly CanvasRect ConsumeBanner = new(Anchor.Center, -320, 226, 320, 266);
    public static readonly CanvasRect ConsumeCancel = new(Anchor.Right, -228, 160, -98, 285);
    /// <summary>
    /// The eye button, top right, over a pick screen ("View Prison Status") and over the Sandbox viewed from it
    /// ("Return to Selection"); measured at 2560x1440.
    /// </summary>
    public static readonly CanvasRect EyeButton = new(Anchor.TopRight, -98, 86, -42, 142);

    public static readonly (string Role, CanvasRect Rect)[] TextRegions =
        [("days", Days), ("objective", Objective), ("title", Title), ("cards", CardText), ("banner", ConsumeBanner)];
}
