namespace Tartarus.Vision;

/// <summary>Fixed-size crops of cells, arrow spots and screen slots, the same at any resolution, for the classifiers.</summary>
public static class CellCrops
{
    public const int Width = 64;
    public const int Height = 96;

    /// <summary>
    /// Resamples the <see cref="SandboxGeometry.CellBox"/> around the padlock point (x, y) at scale k to
    /// <see cref="Width"/> x <see cref="Height"/>, bilinear, in the frame's pixel format. Outside the frame is black.
    /// </summary>
    public static Frame Crop(Frame src, double x, double y, double k)
    {
        var (x0, y0, x1, y1) = SandboxGeometry.CellBox(x, y, k);
        return CropBox(src, x0, y0, x1, y1, Width, Height);
    }

    public const int ContextWidth = 84, ContextHeight = 96;

    /// <summary>The cell with its surroundings (<see cref="SandboxGeometry.ContextBox"/>), ContextWidth x ContextHeight.</summary>
    public static Frame Context(Frame src, double x, double y, double k)
    {
        var (x0, y0, x1, y1) = SandboxGeometry.ContextBox(x, y, k);
        return CropBox(src, x0, y0, x1, y1, ContextWidth, ContextHeight);
    }

    public const int ArrowSize = 48;
    private const double ArrowHalf = 34;

    /// <summary>The spot where the warden's arrow shows when the warden is in cell index n (0-15), ArrowSize square.</summary>
    public static Frame ArrowSpot(Frame src, SandboxLoc loc, int n)
    {
        var (u, v) = SandboxGeometry.Arrow[n];
        var (x, y) = SandboxGeometry.At(loc, u, v);
        double k = SandboxGeometry.ScaleAt(loc, u, v);
        return CropBox(src, x - ArrowHalf * k, y - ArrowHalf * k, x + ArrowHalf * k, y + ArrowHalf * k, ArrowSize, ArrowSize);
    }

    /// <summary>Resamples the box (x0, y0)-(x1, y1) of a frame to width x height, bilinear. Outside the frame is black.</summary>
    public static Frame CropBox(Frame src, double x0, double y0, double x1, double y1, int width, int height)
    {
        var dst = new byte[width * height * 4];
        double sx = (x1 - x0) / width, sy = (y1 - y0) / height;
        var p = src.Pixels;
        int w = src.Width, h = src.Height;
        for (int j = 0; j < height; j++)
        {
            double fy = y0 + (j + 0.5) * sy - 0.5;
            int iy = (int)Math.Floor(fy);
            double ty = fy - iy;
            for (int i = 0; i < width; i++)
            {
                double fx = x0 + (i + 0.5) * sx - 0.5;
                int ix = (int)Math.Floor(fx);
                double tx = fx - ix;
                int o = (j * width + i) * 4;
                for (int ch = 0; ch < 4; ch++)
                {
                    double a = Px(ix, iy, ch), b = Px(ix + 1, iy, ch), c = Px(ix, iy + 1, ch), d = Px(ix + 1, iy + 1, ch);
                    double v = (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
                    dst[o + ch] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
                }
                dst[o + 3] = 255;
            }
        }
        return new Frame(width, height, dst, src.Format, src.Timestamp);

        double Px(int px, int py, int ch) => px < 0 || py < 0 || px >= w || py >= h ? 0 : p[(py * w + px) * 4 + ch];
    }
}
