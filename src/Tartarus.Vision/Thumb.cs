using Tartarus.Vision.Ocr;

namespace Tartarus.Vision;

/// <summary>
/// A coarse brightness picture of a region (each cell the mean of the brightest channel over
/// cellPx x cellPx pixels), to tell whether the region changed between frames. Glows and flames flicker a
/// little everywhere; a changed digit or portrait changes some cells a lot. Small cells catch small text.
/// </summary>
public sealed class Thumb
{
    /// <summary>A cell this much brighter or darker has changed.</summary>
    public const int CellDiff = 40;

    private readonly byte[] _cells;

    private Thumb(PixelBox box, int cellPx, int cols, int rows, byte[] cells)
    {
        Box = box;
        CellPx = cellPx;
        Cols = cols;
        Rows = rows;
        _cells = cells;
    }

    public PixelBox Box { get; }
    public int CellPx { get; }
    public int Cols { get; }
    public int Rows { get; }

    public static Thumb Of(Frame frame, PixelBox box, int cellPx = 2)
    {
        box = box.Clamp(frame.Width, frame.Height);
        int cols = Math.Max(1, box.Width / cellPx), rows = Math.Max(1, box.Height / cellPx);
        var cells = new byte[cols * rows];
        if (box.IsEmpty) return new Thumb(box, cellPx, cols, rows, cells);
        var p = frame.Pixels;
        for (int j = 0; j < rows; j++)
        {
            int y0 = box.Y0 + j * box.Height / rows, y1 = box.Y0 + (j + 1) * box.Height / rows;
            for (int i = 0; i < cols; i++)
            {
                int x0 = box.X0 + i * box.Width / cols, x1 = box.X0 + (i + 1) * box.Width / cols;
                int sum = 0, n = 0;
                for (int y = y0; y < y1; y++)
                {
                    int o = (y * frame.Width + x0) * 4;
                    for (int x = x0; x < x1; x++, o += 4)
                    {
                        sum += Math.Max(p[o], Math.Max(p[o + 1], p[o + 2]));
                        n++;
                    }
                }
                cells[j * cols + i] = (byte)(n > 0 ? sum / n : 0);
            }
        }
        return new Thumb(box, cellPx, cols, rows, cells);
    }

    /// <summary>Same size, at most <paramref name="maxChanged"/> cells changed, and no overall drift.</summary>
    public bool Same(Thumb other, int maxChanged = 2, double maxMeanDiff = 6)
    {
        if (other.Cols != Cols || other.Rows != Rows || other.CellPx != CellPx) return false;
        long total = 0;
        int changed = 0;
        for (int i = 0; i < _cells.Length; i++)
        {
            int d = Math.Abs(_cells[i] - other._cells[i]);
            if (d > CellDiff && ++changed > maxChanged) return false;
            total += d;
        }
        return total <= maxMeanDiff * _cells.Length;
    }
}
