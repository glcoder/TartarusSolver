namespace Tartarus.Vision;

/// <summary>
/// The game's own picture inside a window frame, when it doesn't fill it. Resized by its edges, the game's window
/// may keep the picture it drew before (2026-10-30, a player's report: 720 rows in a 1104x819 window, at the top
/// left, its right side cut off and an even band under it). The picture is read as 16:9, the size the window most
/// likely had; one drawn for a wider window has its UI further right and doesn't read (that player's: about 1308x720).
/// </summary>
public static class GamePicture
{
    /// <summary>How a scan's note about such a picture begins.</summary>
    public const string Note = "The game's picture doesn't fill the window:";

    /// <summary>The least band under the picture, in rows: a dark Sandbox's bottom is no band.</summary>
    private const int MinBand = 16;

    /// <summary>
    /// The frame as the game drew it: the picture at the top left, padded with black on the right to its 16:9 width
    /// (or cut there), and without the band under it; the frame itself when it is no such case. Coordinates in it are
    /// the frame's.
    /// </summary>
    public static Frame Of(Frame frame)
    {
        int w = frame.Width, h = frame.Height;
        // A 16:9 window shows the picture whole.
        if (Math.Abs((double)w / h - 16.0 / 9.0) < 0.02) return frame;
        int band = EvenRows(frame, fromBottom: true);
        // Even rows above as well: bars around a picture the game fit in, which the layout knows.
        if (band < Math.Max(MinBand, h / 20) || EvenRows(frame, fromBottom: false) >= band / 2) return frame;
        int ph = h - band;
        if (ph < h * 2 / 5) return frame;
        // A picture that fits the window leaves dark rows under the Sandbox too (16:10 windows: 60-70 rows): only one
        // a tenth bigger than the window holds is an old one (that player's: 0.80 for 0.69).
        if (ph / ScreenLayout.CanvasHeight < 1.1 * ScreenLayout.DefaultK(w, h)) return frame;
        // The picture's last rows can blur into the band (a JPEG's 718 for 720): a height of 9's multiple near it.
        if (Math.Abs((int)Math.Round(ph / 9.0) * 9 - ph) <= 3) ph = Math.Min(h, (int)Math.Round(ph / 9.0) * 9);
        int pw = (int)Math.Round(ph * 16.0 / 9.0);
        var pixels = new byte[pw * ph * 4];
        int copy = Math.Min(w, pw) * 4;
        for (int y = 0; y < ph; y++)
        {
            Array.Copy(frame.Pixels, y * frame.Stride, pixels, y * pw * 4, copy);
            for (int x = copy + 3; x < pw * 4; x += 4) pixels[y * pw * 4 + x] = 255;
        }
        return new Frame(pw, ph, pixels, frame.Format, frame.Timestamp);
    }

    /// <summary>How many rows at the bottom (or top) are one even color.</summary>
    private static int EvenRows(Frame frame, bool fromBottom)
    {
        int w = frame.Width, h = frame.Height, n = 0;
        int[]? first = null;
        for (int i = 0; i < h / 2; i++)
        {
            int y = fromBottom ? h - 1 - i : i;
            var (mean, spread) = Row(frame, y);
            first ??= mean;
            if (spread > 6 || Math.Abs(mean[0] - first[0]) + Math.Abs(mean[1] - first[1]) + Math.Abs(mean[2] - first[2]) > 9) break;
            n++;
        }
        return n;
    }

    /// <summary>A row's mean color and its mean distance from it, over every fourth pixel.</summary>
    private static (int[] Mean, double Spread) Row(Frame frame, int y)
    {
        var p = frame.Pixels;
        int start = y * frame.Stride, count = 0;
        long b = 0, g = 0, r = 0;
        for (int x = 0; x < frame.Width; x += 4, count++)
        {
            int o = start + x * 4;
            (b, g, r) = (b + p[o], g + p[o + 1], r + p[o + 2]);
        }
        var mean = new[] { (int)(b / count), (int)(g / count), (int)(r / count) };
        double spread = 0;
        for (int x = 0; x < frame.Width; x += 4)
        {
            int o = start + x * 4;
            spread += Math.Abs(p[o] - mean[0]) + Math.Abs(p[o + 1] - mean[1]) + Math.Abs(p[o + 2] - mean[2]);
        }
        return (mean, spread / count);
    }
}
