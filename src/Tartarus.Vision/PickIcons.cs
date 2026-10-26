using System.Text.Json;

namespace Tartarus.Vision;

/// <summary>
/// A pick screen's cards told by their pictures, in any language of the game: the Torture Instruments' icons
/// (models/instruments/pick, made by tools/train/pickicons.py) on their cards, the Warden's Treasures' pictures
/// (models/treasures, cut from the screen by WardenTreasures.SaveMissingIcons) on theirs.
/// </summary>
public sealed class PickIcons
{
    private const int Search = 4;

    /// <summary>
    /// Least likeness to take a card for one: instruments' cards on pick screens score 0.7 and up (lower only while
    /// fading in), other screens' dark corners up to 0.58; treasures' 0.92 and up, the Objective's popup 0.55.
    /// </summary>
    public double Threshold { get; private init; }

    private readonly List<(string Id, float[] Template)> _templates = [];
    private int _size, _unit;
    private double _dx, _cy;

    /// <summary>The instruments' icons as the pick shows them; null without models/instruments/pick.</summary>
    public static PickIcons? Instruments()
    {
        string? dir = NameMatch.FindModelDir(Path.Combine("instruments", "pick"), "pick.json");
        if (dir == null) return null;
        using var place = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "pick.json")));
        var root = place.RootElement;
        var icons = new PickIcons
        {
            Threshold = 0.65,
            _size = root.GetProperty("pixels").GetInt32(),
            _unit = root.GetProperty("unit").GetInt32(),
            _dx = root.GetProperty("dx").GetDouble(),
            _cy = root.GetProperty("cy").GetDouble(),
        };
        foreach (var instrument in TortureInstruments.All)
        {
            string path = Path.Combine(dir, instrument.Id + ".png");
            if (File.Exists(path) && ImageIO.Load(path) is { } f && f.Width == icons._size && f.Height == icons._size)
                icons._templates.Add((instrument.Id, CardIcons.Normalized(CardIcons.Rgb(f))));
        }
        return icons._templates.Count > 0 ? icons : null;
    }

    /// <summary>The treasures' pictures, cut from the screen at <see cref="ScreenLayout.TreasureIcon"/>; null without any.</summary>
    public static PickIcons? Treasures()
    {
        string? dir = WardenTreasures.FindIconDir();
        if (dir == null) return null;
        // TreasureIcon: 160 canvas units square, centred over the card, 152 above the screen's middle.
        var icons = new PickIcons { Threshold = 0.8, _size = 40, _unit = 4, _dx = 0, _cy = -152 };
        foreach (var path in Directory.EnumerateFiles(dir, "*.png"))
        {
            var f = ImageIO.Load(path);
            if (f.Width != icons._size * icons._unit || f.Height != f.Width) continue;
            var small = CardIcons.Sample(f, 0, 0, f.Width, f.Height, icons._size, icons._size, icons._unit);
            icons._templates.Add((Path.GetFileNameWithoutExtension(path), CardIcons.Normalized(CardIcons.Rgb(small))));
        }
        return icons._templates.Count > 0 ? icons : null;
    }

    /// <summary>The card centred <paramref name="cardX"/> canvas units right of the middle: the likest id (null under the threshold) and its likeness.</summary>
    public (string? Id, double Score) Classify(Frame frame, ScreenLayout layout, double cardX)
    {
        double half = (_size / 2.0 + Search) * _unit, x = cardX + _dx;
        var (x0, y0) = layout.ToPixels(Anchor.Center, x - half, _cy - half);
        var (x1, y1) = layout.ToPixels(Anchor.Center, x + half, _cy + half);
        int side = _size + 2 * Search;
        var rgb = CardIcons.Rgb(CardIcons.Sample(frame, x0, y0, x1, y1, side, side, _unit));
        string? best = null;
        double bestScore = -1;
        foreach (var (id, template) in _templates)
        {
            double score = CardIcons.BestMatch(rgb, side, side, template, _size).Score;
            if (score > bestScore) (bestScore, best) = (score, id);
        }
        return (bestScore >= Threshold ? best : null, bestScore);
    }
}
