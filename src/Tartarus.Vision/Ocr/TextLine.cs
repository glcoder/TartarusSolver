namespace Tartarus.Vision.Ocr;

/// <summary>A pixel rectangle in frame coordinates, [X0, X1) x [Y0, Y1).</summary>
public readonly record struct PixelBox(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0;
    public int Height => Y1 - Y0;
    public double CenterX => (X0 + X1) / 2.0;
    public double CenterY => (Y0 + Y1) / 2.0;
    public bool IsEmpty => X1 <= X0 || Y1 <= Y0;

    public PixelBox Clamp(int width, int height) =>
        new(Math.Clamp(X0, 0, width), Math.Clamp(Y0, 0, height), Math.Clamp(X1, 0, width), Math.Clamp(Y1, 0, height));

    public bool Contains(double x, double y) => x >= X0 && x < X1 && y >= Y0 && y < Y1;

    public override string ToString() => $"[{X0},{Y0} {Width}x{Height}]";
}

/// <summary>One recognized character and where it sits along the line, in frame pixels.</summary>
public readonly record struct TextChar(char Char, double X0, double X1, float Conf);

/// <summary>
/// A line of text found on screen. <see cref="Box"/> is the detector's region (grown by a margin); <see cref="Ink"/> is
/// the letters' own bounding box inside it.
/// </summary>
public sealed class TextLine
{
    public required string Text { get; init; }
    public required PixelBox Box { get; init; }
    public required PixelBox Ink { get; init; }
    /// <summary>Mean probability of the recognized characters.</summary>
    public required float Conf { get; init; }
    /// <summary>Mean text probability of the detector's region.</summary>
    public required float DetScore { get; init; }
    public required IReadOnlyList<TextChar> Chars { get; init; }

    public override string ToString() => $"\"{Text}\" {Ink} conf {Conf:0.00}";
}
