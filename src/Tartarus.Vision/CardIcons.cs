namespace Tartarus.Vision;

/// <summary>
/// What a card under the Sandbox is, from its picture rather than its words, so in any language of the game: each
/// decision (Consume, Hunt, Replace, End, Instigate, Bestow) has its own round icon on its own background, and the
/// Interrogation Room's Destination card its lit doorway. A card like none of them is a Destination to a cell (its
/// number still comes from OCR: digits read in any language). Templates are models/cards/&lt;kind&gt;.png, made from
/// English frames by tartarus-scan card-templates.
/// </summary>
public sealed class CardIcons
{
    /// <summary>The kinds with a template; a card's kind is one of these or null (a Destination to a cell).</summary>
    public static readonly string[] Kinds = ["consume", "hunt", "replace", "end", "instigate", "bestow", "room"];

    /// <summary>Template side in pixels, 2 canvas units each.</summary>
    public const int Size = 36;
    private const double Unit = 2;
    // The icon, centred over the card's text (measured at 1920x1200): canvas units from the screen's middle.
    private const double IconHalf = Size * Unit / 2, IconY = 222;
    /// <summary>
    /// How far the icon may sit from where it rests, in template pixels, sideways and up or down: a card under the
    /// pointer rises, a row of two sits lower, cards slide in.
    /// </summary>
    public const int Search = 5, SearchY = 12;
    /// <summary>Least likeness (normalized cross-correlation) to take a card for a kind.</summary>
    public const double Threshold = 0.6;

    private readonly List<(string Kind, float[] Template)> _templates = [];

    public IReadOnlyList<string> Loaded => _templates.Select(t => t.Kind).ToList();

    public static string? FindDir() => NameMatch.FindModelDir("cards", null);

    /// <summary>The templates in <paramref name="dir"/> (models/cards); null when there are none.</summary>
    public static CardIcons? Load(string? dir = null)
    {
        dir ??= FindDir();
        if (dir == null) return null;
        var icons = new CardIcons();
        foreach (var kind in Kinds)
        {
            string path = Path.Combine(dir, kind + ".png");
            if (!File.Exists(path)) continue;
            var frame = ImageIO.Load(path);
            if (frame.Width != Size || frame.Height != Size) continue;
            icons._templates.Add((kind, Normalized(Rgb(frame))));
        }
        return icons._templates.Count > 0 ? icons : null;
    }

    /// <summary>The area around the icon of the card centred <paramref name="cx"/> canvas units right of the middle: Size + 2 Search wide, Size + 2 SearchY high.</summary>
    public static Frame Patch(Frame frame, ScreenLayout layout, double cx)
    {
        double halfX = IconHalf + Search * Unit, halfY = IconHalf + SearchY * Unit;
        var (x0, y0) = layout.ToPixels(Anchor.Center, cx - halfX, IconY - halfY);
        var (x1, y1) = layout.ToPixels(Anchor.Center, cx + halfX, IconY + halfY);
        return Sample(frame, x0, y0, x1, y1, Size + 2 * Search, Size + 2 * SearchY, (int)Unit);
    }

    /// <summary>
    /// A box of the frame at <paramref name="w"/> x <paramref name="h"/>: sampled at <paramref name="factor"/> times
    /// that, then averaged factor x factor, so a fair picture at any resolution.
    /// </summary>
    public static Frame Sample(Frame frame, double x0, double y0, double x1, double y1, int w, int h, int factor)
    {
        var fine = CellCrops.CropBox(frame, x0, y0, x1, y1, w * factor, h * factor);
        var dst = new byte[w * h * 4];
        int area = factor * factor;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int ch = 0; ch < 4; ch++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < factor; dy++)
                        for (int dx = 0; dx < factor; dx++) sum += fine.Pixels[((factor * y + dy) * fine.Width + factor * x + dx) * 4 + ch];
                    dst[(y * w + x) * 4 + ch] = (byte)((sum + area / 2) / area);
                }
        return new Frame(w, h, dst, frame.Format, frame.Timestamp);
    }

    /// <summary>The card's kind (null: none alike, a Destination to a cell), its likeness, and the runner-up's.</summary>
    public (string? Kind, double Score, double Second) Classify(Frame frame, ScreenLayout layout, double cx)
    {
        var patch = Patch(frame, layout, cx);
        var rgb = Rgb(patch);
        string? best = null;
        double bestScore = -1, second = -1;
        foreach (var (kind, template) in _templates)
        {
            double score = BestMatch(rgb, patch.Width, patch.Height, template).Score;
            if (score > bestScore) (second, bestScore, best) = (bestScore, score, kind);
            else if (score > second) second = score;
        }
        return (bestScore >= Threshold ? best : null, bestScore, second);
    }

    /// <summary>The best normalized cross-correlation of a <paramref name="size"/>² template over a patch, and where.</summary>
    public static (double Score, int Dx, int Dy) BestMatch(float[] rgb, int width, int height, float[] template, int size = Size)
    {
        int n = size * size * 3;
        double best = -1;
        int bx = 0, by = 0;
        for (int oy = 0; oy + size <= height; oy++)
            for (int ox = 0; ox + size <= width; ox++)
            {
                double sum = 0, sq = 0, dot = 0;
                for (int y = 0; y < size; y++)
                {
                    int row = ((oy + y) * width + ox) * 3, trow = y * size * 3;
                    for (int i = 0; i < size * 3; i++)
                    {
                        float v = rgb[row + i];
                        sum += v;
                        sq += v * v;
                        dot += v * template[trow + i];
                    }
                }
                double var = sq - sum * sum / n;
                if (var <= 1e-6) continue;
                // The template is zero-mean, so the patch's mean drops out of the dot product.
                double score = dot / Math.Sqrt(var);
                if (score > best) (best, bx, by) = (score, ox, oy);
            }
        return (best, bx, by);
    }

    /// <summary>R, G, B as floats 0-1, row by row.</summary>
    public static float[] Rgb(Frame f)
    {
        var rgb = new float[f.Width * f.Height * 3];
        bool bgra = f.Format == PixelFormat.Bgra32;
        for (int i = 0; i < f.Width * f.Height; i++)
        {
            rgb[i * 3] = f.Pixels[i * 4 + (bgra ? 2 : 0)] / 255f;
            rgb[i * 3 + 1] = f.Pixels[i * 4 + 1] / 255f;
            rgb[i * 3 + 2] = f.Pixels[i * 4 + (bgra ? 0 : 2)] / 255f;
        }
        return rgb;
    }

    /// <summary>Zero mean, unit norm: a template ready for <see cref="BestMatch"/>.</summary>
    public static float[] Normalized(float[] v)
    {
        double mean = v.Average(x => (double)x), norm = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)));
        return v.Select(x => (float)((x - mean) / Math.Max(norm, 1e-6))).ToArray();
    }
}
