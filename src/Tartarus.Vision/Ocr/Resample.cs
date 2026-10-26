namespace Tartarus.Vision.Ocr;

/// <summary>
/// Resamples a region of a frame straight into a planar float tensor in B, G, R plane order (the order
/// PaddleOCR models were trained on, from OpenCV), normalized per channel as (v / 255 - mean) / std.
/// Shrinking averages the source pixels each output pixel covers; enlarging is bilinear.
/// </summary>
internal static class Resample
{
    public static void ToBgrPlanes(Frame src, double x0, double y0, double x1, double y1, int outW, int outH,
        Span<float> dest, int destStride, ReadOnlySpan<float> mean, ReadOnlySpan<float> std, int destRows = 0)
    {
        // destRows: rows per plane when the tensor is taller than the picture (padding below it).
        int planeSize = destStride * Math.Max(outH, destRows);
        int bOff = src.Format == PixelFormat.Bgra32 ? 0 : 2;
        int rOff = 2 - bOff;
        double sx = (x1 - x0) / outW, sy = (y1 - y0) / outH;
        var p = src.Pixels;
        int w = src.Width, h = src.Height;
        Span<float> scale = [1f / (255f * std[0]), 1f / (255f * std[1]), 1f / (255f * std[2])];
        Span<float> shift = [mean[0] / std[0], mean[1] / std[1], mean[2] / std[2]];

        for (int j = 0; j < outH; j++)
        {
            double fy0 = y0 + j * sy, fy1 = fy0 + sy;
            for (int i = 0; i < outW; i++)
            {
                double fx0 = x0 + i * sx, fx1 = fx0 + sx;
                float b, g, r;
                if (sx > 1.25 || sy > 1.25)
                {
                    // Box average over the source pixels whose centers fall in the cell.
                    int ax = Math.Max(0, (int)Math.Ceiling(fx0 - 0.5)), bx = Math.Min(w - 1, (int)Math.Ceiling(fx1 - 0.5) - 1);
                    int ay = Math.Max(0, (int)Math.Ceiling(fy0 - 0.5)), by = Math.Min(h - 1, (int)Math.Ceiling(fy1 - 0.5) - 1);
                    if (bx < ax) bx = ax = Math.Clamp((int)((fx0 + fx1) / 2), 0, w - 1);
                    if (by < ay) by = ay = Math.Clamp((int)((fy0 + fy1) / 2), 0, h - 1);
                    int sb = 0, sg = 0, sr = 0;
                    for (int yy = ay; yy <= by; yy++)
                    {
                        int o = (yy * w + ax) * 4;
                        for (int xx = ax; xx <= bx; xx++, o += 4)
                        {
                            sb += p[o + bOff];
                            sg += p[o + 1];
                            sr += p[o + rOff];
                        }
                    }
                    float n = (bx - ax + 1) * (by - ay + 1);
                    b = sb / n; g = sg / n; r = sr / n;
                }
                else
                {
                    double fx = (fx0 + fx1) / 2 - 0.5, fy = (fy0 + fy1) / 2 - 0.5;
                    int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                    float tx = (float)(fx - ix), ty = (float)(fy - iy);
                    int x0c = Math.Clamp(ix, 0, w - 1), x1c = Math.Clamp(ix + 1, 0, w - 1);
                    int y0c = Math.Clamp(iy, 0, h - 1), y1c = Math.Clamp(iy + 1, 0, h - 1);
                    int o00 = (y0c * w + x0c) * 4, o01 = (y0c * w + x1c) * 4, o10 = (y1c * w + x0c) * 4, o11 = (y1c * w + x1c) * 4;
                    b = Lerp(p[o00 + bOff], p[o01 + bOff], p[o10 + bOff], p[o11 + bOff], tx, ty);
                    g = Lerp(p[o00 + 1], p[o01 + 1], p[o10 + 1], p[o11 + 1], tx, ty);
                    r = Lerp(p[o00 + rOff], p[o01 + rOff], p[o10 + rOff], p[o11 + rOff], tx, ty);
                }
                int d = j * destStride + i;
                dest[d] = b * scale[0] - shift[0];
                dest[planeSize + d] = g * scale[1] - shift[1];
                dest[2 * planeSize + d] = r * scale[2] - shift[2];
            }
        }
    }

    private static float Lerp(byte a, byte b, byte c, byte d, float tx, float ty) =>
        (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
}
