namespace Tartarus.Vision;

/// <summary>Pixel layout of a <see cref="Frame"/> buffer.</summary>
public enum PixelFormat
{
    /// <summary>B, G, R, A bytes (what Windows.Graphics.Capture delivers).</summary>
    Bgra32,
    /// <summary>R, G, B, A bytes (what scan.js and PNG files use).</summary>
    Rgba32,
}

/// <summary>A tightly packed 32-bit image (stride == width * 4).</summary>
public sealed class Frame
{
    public Frame(int width, int height, byte[] pixels, PixelFormat format, DateTimeOffset? timestamp = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length < width * height * 4) throw new ArgumentException("Pixel buffer is too small.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
        Format = format;
        Timestamp = timestamp ?? DateTimeOffset.Now;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public PixelFormat Format { get; }
    public DateTimeOffset Timestamp { get; }
    public int Stride => Width * 4;

    /// <summary>Copies the pixels into <paramref name="dest"/> as RGBA, swizzling if needed.</summary>
    public void CopyRgbaTo(Span<byte> dest)
    {
        int n = Width * Height * 4;
        if (dest.Length < n) throw new ArgumentException("Destination is too small.", nameof(dest));
        var src = Pixels.AsSpan(0, n);
        if (Format == PixelFormat.Rgba32)
        {
            src.CopyTo(dest);
            return;
        }
        SwapRedBlue(src, dest);
    }

    public byte[] ToRgba()
    {
        var buf = new byte[Width * Height * 4];
        CopyRgbaTo(buf);
        return buf;
    }

    /// <summary>BGRA &lt;-&gt; RGBA (the operation is its own inverse).</summary>
    public static void SwapRedBlue(ReadOnlySpan<byte> src, Span<byte> dest)
    {
        // Process 4 pixels at a time through uint reads; the JIT/AOT compiler vectorizes poorly here,
        // but 1080p is ~2M pixels and this runs in a few ms, which is negligible next to the scan itself.
        var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(src);
        var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dest);
        for (int i = 0; i < s.Length; i++)
        {
            uint p = s[i];
            d[i] = (p & 0xFF00FF00u) | ((p & 0x00FF0000u) >> 16) | ((p & 0x000000FFu) << 16);
        }
    }

    /// <summary>Returns a sub-image (clamped to the frame) in the same pixel format.</summary>
    public Frame Crop(int x, int y, int w, int h)
    {
        int x0 = Math.Clamp(x, 0, Width), y0 = Math.Clamp(y, 0, Height);
        int x1 = Math.Clamp(x + w, 0, Width), y1 = Math.Clamp(y + h, 0, Height);
        int cw = Math.Max(1, x1 - x0), ch = Math.Max(1, y1 - y0);
        var buf = new byte[cw * ch * 4];
        for (int row = 0; row < ch && y0 + row < Height; row++)
        {
            int copy = Math.Min(cw, Width - x0) * 4;
            if (copy <= 0) break;
            Buffer.BlockCopy(Pixels, ((y0 + row) * Width + x0) * 4, buf, row * cw * 4, copy);
        }
        return new Frame(cw, ch, buf, Format, Timestamp);
    }
}
