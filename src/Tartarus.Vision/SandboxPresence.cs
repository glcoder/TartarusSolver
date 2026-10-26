namespace Tartarus.Vision;

/// <summary>
/// Is the Sandbox on screen? The Sandbox's stonework (cell walls, the courtyard's edges) shows on every Sandbox
/// screen, whatever lies over it: the red of a Consume pick, a day's banner, a fade. Its edges are compared
/// with a template averaged from our own Sandbox frames: the Sandbox region's horizontal and vertical brightness
/// differences (with their signs: the gradient's direction tells the cells' grid from the lobby's arches, its
/// size alone doesn't), at <see cref="Width"/> x <see cref="Height"/>, zero-mean and unit-length, dotted with the
/// template. Sandbox screens score 0.27 and up (a Consume pick's red 0.45, a day's banner 0.33); the lobby,
/// instrument and treasure picks, story popups and the game world 0.06 at most (291 frames).
/// </summary>
public sealed class SandboxPresence
{
    public const int Width = 184, Height = 104;
    public const string FileName = "template.bin";
    /// <summary>The Sandbox's region in canvas units (1600x900, from the middle of the screen).</summary>
    public static readonly CanvasRect Region = new(Anchor.Center, 340 - 800, 40 - 450, 1260 - 800, 560 - 450);
    public const float DefaultThreshold = 0.17f;
    private const int Length = 2 * Width * Height;

    private readonly float[] _template;

    public SandboxPresence(float[] template)
    {
        if (template.Length != Length) throw new ArgumentException($"Template must have {Length} values (rebuild it: tartarus-scan sandbox-template).");
        _template = template;
    }

    public float Threshold { get; set; } = DefaultThreshold;

    public static SandboxPresence Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var t = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, t, 0, t.Length * 4);
        return new SandboxPresence(t);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[_template.Length * 4];
        Buffer.BlockCopy(_template, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>models/sandbox/template.bin next to the executable or in the repository it runs from; null if there is none yet.</summary>
    public static string? FindTemplate() => AppFiles.Find("models", "sandbox", FileName);

    public float Score(Frame frame, ScreenLayout layout) => Score(Features(frame, layout));

    /// <summary>Match of features from <see cref="Features"/> with the template.</summary>
    public float Score(float[] features) => Dot(features, _template);

    public bool IsBoard(Frame frame, ScreenLayout layout) => Score(frame, layout) >= Threshold;

    /// <summary>The template: the normalized mean of the given frames' features.</summary>
    public static SandboxPresence Build(IEnumerable<float[]> features)
    {
        var sum = new float[Length];
        int n = 0;
        foreach (var f in features)
        {
            for (int i = 0; i < sum.Length; i++) sum[i] += f[i];
            n++;
        }
        if (n == 0) throw new InvalidOperationException("No Sandbox frames to build the template from.");
        Normalize(sum);
        return new SandboxPresence(sum);
    }

    public static float Dot(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return (float)s;
    }

    /// <summary>The Sandbox region box-averaged down to Width x Height, its x and y differences, zero-mean unit-length.</summary>
    public static float[] Features(Frame frame, ScreenLayout layout)
    {
        var box = layout.ToPixels(Region);
        var v = new float[Width * Height];
        var p = frame.Pixels;
        int fw = frame.Width;
        // Brightest channel, averaged over each output cell's pixels.
        for (int j = 0; j < Height; j++)
        {
            int y0 = box.Y0 + j * box.Height / Height, y1 = Math.Max(y0 + 1, box.Y0 + (j + 1) * box.Height / Height);
            for (int i = 0; i < Width; i++)
            {
                int x0 = box.X0 + i * box.Width / Width, x1 = Math.Max(x0 + 1, box.X0 + (i + 1) * box.Width / Width);
                long sum = 0;
                int n = 0;
                for (int y = y0; y < y1 && y < frame.Height; y++)
                {
                    int o = (y * fw + x0) * 4;
                    for (int x = x0; x < x1 && x < fw; x++, o += 4)
                    {
                        sum += Math.Max(p[o], Math.Max(p[o + 1], p[o + 2]));
                        n++;
                    }
                }
                v[j * Width + i] = n > 0 ? (float)sum / n : 0;
            }
        }
        // x differences first, then y differences; zero on the border.
        var g = new float[Length];
        int plane = Width * Height;
        for (int j = 1; j < Height - 1; j++)
            for (int i = 1; i < Width - 1; i++)
            {
                g[j * Width + i] = v[j * Width + i + 1] - v[j * Width + i - 1];
                g[plane + j * Width + i] = v[(j + 1) * Width + i] - v[(j - 1) * Width + i];
            }
        Normalize(g);
        return g;
    }

    private static void Normalize(float[] a)
    {
        double mean = 0;
        foreach (var x in a) mean += x;
        mean /= a.Length;
        double norm = 0;
        for (int i = 0; i < a.Length; i++)
        {
            a[i] -= (float)mean;
            norm += a[i] * a[i];
        }
        float k = norm > 1e-12 ? (float)(1 / Math.Sqrt(norm)) : 0;
        for (int i = 0; i < a.Length; i++) a[i] *= k;
    }
}
