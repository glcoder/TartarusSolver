namespace Tartarus.Vision.Ocr;

/// <summary>
/// The letters' own bounding box inside a detector region. The detector's regions are grown by a margin
/// (DBNet's unclip), but scan.js lays panels out from a label's exact letter edges. Game text is lighter
/// than what is behind it, colored or not, so ink is any pixel well above the region's typical brightness.
/// </summary>
internal static class InkBounds
{
    public static PixelBox Find(Frame frame, PixelBox box)
    {
        box = box.Clamp(frame.Width, frame.Height);
        int w = box.Width, h = box.Height;
        if (w < 3 || h < 3) return box;
        var p = frame.Pixels;
        var v = new byte[w * h];
        Span<int> hist = stackalloc int[256];
        for (int y = 0; y < h; y++)
        {
            int o = ((box.Y0 + y) * frame.Width + box.X0) * 4;
            for (int x = 0; x < w; x++, o += 4)
            {
                byte m = Math.Max(p[o], Math.Max(p[o + 1], p[o + 2]));
                v[y * w + x] = m;
                hist[m]++;
            }
        }
        int median = Percentile(hist, w * h, 0.5), top = Percentile(hist, w * h, 0.98);
        if (top - median < 24) return box;
        int threshold = (median + top) / 2;

        // Rows with a few lit pixels, then columns lit within those rows.
        int minRow = Math.Max(2, w / 50);
        int y0 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
        {
            int n = 0;
            for (int x = 0; x < w; x++) if (v[y * w + x] > threshold) n++;
            if (n < minRow) continue;
            if (y0 < 0) y0 = y;
            y1 = y;
        }
        if (y0 < 0) return box;
        int x0 = -1, x1 = -1;
        for (int x = 0; x < w; x++)
        {
            bool lit = false;
            for (int y = y0; y <= y1 && !lit; y++) lit = v[y * w + x] > threshold;
            if (!lit) continue;
            if (x0 < 0) x0 = x;
            x1 = x;
        }
        return new PixelBox(box.X0 + x0, box.Y0 + y0, box.X0 + x1 + 1, box.Y0 + y1 + 1);
    }

    private static int Percentile(ReadOnlySpan<int> hist, int total, double q)
    {
        long want = (long)(total * q), seen = 0;
        for (int i = 0; i < 256; i++)
        {
            seen += hist[i];
            if (seen > want) return i;
        }
        return 255;
    }
}
