namespace Tartarus.Vision;

/// <summary>
/// The run's theme from its icon in the pendant at the bottom left of the Sandbox: there all run in any language, none
/// without a theme, where the theme's name shows only on the scroll before the run. Templates are models/themes/&lt;id&gt;.png,
/// the game's own art (UI_S16Gameplay_SpecialTheme_01-04) with its transparency: only the drawn pixels are compared.
/// </summary>
public sealed class ThemeIcons
{
    /// <summary>Template side in pixels, 2 canvas units each.</summary>
    public const int Size = 45;
    private const int Unit = 2, Search = 6;
    // The icon's middle: canvas units from the window's left, and down from its middle.
    private const double CenterX = 105, CenterY = 231.25;
    /// <summary>Least likeness to take the pendant for a theme's.</summary>
    public const double Threshold = 0.7;

    private readonly List<(string Id, int[] Offsets, float[] Values)> _templates = [];

    public IReadOnlyList<string> Loaded => _templates.Select(t => t.Id).ToList();

    /// <summary>The templates in <paramref name="dir"/> (models/themes); null when there are none.</summary>
    public static ThemeIcons? Load(string? dir = null)
    {
        dir ??= NameMatch.FindModelDir("themes", null);
        if (dir == null) return null;
        var icons = new ThemeIcons();
        foreach (var theme in RunThemes.All)
        {
            string path = Path.Combine(dir, theme.Id + ".png");
            if (!File.Exists(path)) continue;
            var image = ImageIO.Load(path);
            if (image.Width != Size || image.Height != Size) continue;
            icons._templates.Add(Template(theme.Id, image));
        }
        return icons._templates.Count > 0 ? icons : null;
    }

    /// <summary>The drawn pixels' places in an R, G, B row by row array, and their values, zero-mean and of length one.</summary>
    private static (string, int[], float[]) Template(string id, Frame image)
    {
        var offsets = new List<int>();
        var values = new List<float>();
        for (int i = 0; i < Size * Size; i++)
        {
            if (image.Pixels[i * 4 + 3] < 128) continue;
            for (int ch = 0; ch < 3; ch++)
            {
                offsets.Add(i * 3 + ch);
                values.Add(image.Pixels[i * 4 + ch] / 255f);
            }
        }
        var v = values.ToArray();
        float mean = v.Average();
        double norm = 0;
        for (int i = 0; i < v.Length; i++)
        {
            v[i] -= mean;
            norm += v[i] * v[i];
        }
        norm = Math.Sqrt(norm);
        for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
        return (id, offsets.ToArray(), v);
    }

    /// <summary>The pendant's theme (null: none alike), its likeness, and the runner-up's.</summary>
    public (string? Id, double Score, double Second) Classify(Frame frame, ScreenLayout layout)
    {
        double half = (Size / 2.0 + Search) * Unit;
        var (x0, y0) = layout.ToPixels(Anchor.Left, CenterX - half, CenterY - half);
        var (x1, y1) = layout.ToPixels(Anchor.Left, CenterX + half, CenterY + half);
        int side = Size + 2 * Search;
        var rgb = CardIcons.Rgb(CardIcons.Sample(frame, x0, y0, x1, y1, side, side, Unit));
        string? best = null;
        double bestScore = -1, second = -1;
        foreach (var (id, offsets, values) in _templates)
        {
            double score = BestMatch(rgb, side, offsets, values);
            if (score > bestScore) (second, bestScore, best) = (bestScore, score, id);
            else if (score > second) second = score;
        }
        return (bestScore >= Threshold ? best : null, bestScore, second);
    }

    /// <summary>The best normalized cross-correlation of a template over the drawn pixels only, the patch's mean taken over them too.</summary>
    private static double BestMatch(float[] rgb, int side, int[] offsets, float[] values)
    {
        int n = offsets.Length;
        double best = -1;
        for (int oy = 0; oy + Size <= side; oy++)
            for (int ox = 0; ox + Size <= side; ox++)
            {
                // Template rows are Size wide, the patch's side: a template pixel's place maps row by row.
                double sum = 0, sq = 0, dot = 0;
                for (int i = 0; i < n; i++)
                {
                    int o = offsets[i], pixel = o / 3;
                    float v = rgb[((oy + pixel / Size) * side + ox + pixel % Size) * 3 + o % 3];
                    sum += v;
                    sq += v * v;
                    dot += v * values[i];
                }
                double var = sq - sum * sum / n;
                if (var <= 1e-6) continue;
                double score = dot / Math.Sqrt(var);
                if (score > best) best = score;
            }
        return best;
    }
}
