using StbImageSharp;
using StbImageWriteSharp;

namespace Tartarus.Vision;

/// <summary>PNG/JPEG loading and PNG saving (pure managed, AOT-safe).</summary>
public static class ImageIO
{
    public static Frame Load(string path)
    {
        using var fs = File.OpenRead(path);
        var img = ImageResult.FromStream(fs, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        return new Frame(img.Width, img.Height, img.Data, PixelFormat.Rgba32, File.GetLastWriteTime(path));
    }

    public static void SavePng(Frame frame, string path)
    {
        var rgba = frame.Format == PixelFormat.Rgba32 ? frame.Pixels : frame.ToRgba();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Write-then-rename: the app closed mid-save leaves no empty or half-written image behind.
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
            new ImageWriter().WritePng(rgba, frame.Width, frame.Height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, fs);
        File.Move(tmp, path, overwrite: true);
    }
}
