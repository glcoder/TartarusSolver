namespace Tartarus.Vision;

/// <summary>
/// Sandbox grid to image pixels, as in js/scan.js: a fixed homography H0 maps the grid (u, v) of a cell's
/// padlock to the reference screenshot, and a located Sandbox is that scaled by s and shifted by (tx, ty).
/// </summary>
public static class SandboxGeometry
{
    public static readonly double[] H0 =
        [119.44981, -9.9567366, 358.07993, -0.26960321, 90.497559, 136.75498, -0.00012281694, -0.015743494, 1];

    /// <summary>Cells 01-16 clockwise from the Interrogation Room, as grid columns and rows.</summary>
    public static readonly (int U, int V)[] Ring =
        [(0, 0), (1, 0), (2, 0), (3, 0), (4, 0), (5, 0), (5, 1), (5, 2), (5, 3), (4, 3), (3, 3), (2, 3), (1, 3), (0, 3), (0, 2), (0, 1)];

    public const double Pitch0 = 119.45;

    /// <summary>
    /// Where the warden's arrow points from when the warden is in cell n (index 0-15), in grid units: on the
    /// courtyard side of the cell (measured in js/scan.js; corners estimated there).
    /// </summary>
    public static readonly (double U, double V)[] Arrow =
        [(0.56, 0.2), (1, 0.25), (2, 0.25), (3, 0.25), (4, 0.25), (4.44, 0.2), (4.42, 0.57), (4.42, 1.57), (4.45, 1.95), (4, 1.9), (3, 1.9), (2, 1.9), (1, 1.9), (0.55, 1.95), (0.58, 1.57), (0.58, 0.57)];

    public static (double X, double Y) Project(double[] h, double u, double v)
    {
        double w = h[6] * u + h[7] * v + h[8];
        return ((h[0] * u + h[1] * v + h[2]) / w, (h[3] * u + h[4] * v + h[5]) / w);
    }

    public static (double X, double Y) At(SandboxLoc loc, double u, double v)
    {
        var (x, y) = Project(H0, u, v);
        return (loc.S * x + loc.Tx, loc.S * y + loc.Ty);
    }

    public static double ScaleAt(SandboxLoc loc, double u, double v)
    {
        var (x0, y0) = Project(H0, u - 0.5, v);
        var (x1, y1) = Project(H0, u + 0.5, v);
        return loc.S * Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) / Pitch0;
    }

    /// <summary>
    /// Box around a cell's portrait, frame, Consumable jaws and padlock, in image pixels, relative to the padlock point
    /// (x, y) and scale k: x ± 36k, y - 100k .. y + 8k. Aspect 2:3.
    /// </summary>
    public static (double X0, double Y0, double X1, double Y1) CellBox(double x, double y, double k)
        => (x - 36 * k, y - 100 * k, x + 36 * k, y + 8 * k);

    /// <summary>
    /// Tighter box around the portrait's frame and padlock (x ± 30k, y - 66k .. y + 4k) for drawing and
    /// clicking: the wide <see cref="CellBox"/> of neighboring side cells overlaps.
    /// </summary>
    public static (double X0, double Y0, double X1, double Y1) FrameBox(double x, double y, double k)
        => (x - 30 * k, y - 66 * k, x + 30 * k, y + 4 * k);

    /// <summary>
    /// A cell with its surroundings, for the cell classifier: portrait, frame, Consumable jaws, padlock, and the
    /// Prison Incident "!" that sits 45-55 units right of the padlock: x - 40k .. x + 64k, y - 104k .. y + 12k.
    /// </summary>
    public static (double X0, double Y0, double X1, double Y1) ContextBox(double x, double y, double k)
        => (x - 40 * k, y - 104 * k, x + 64 * k, y + 12 * k);

    /// <summary>Portrait region scan.js matches faces on (FACE box): x - 16k .. x + 16k, y - 58k .. y - 20k.</summary>
    public static (double X0, double Y0, double X1, double Y1) FaceBox(double x, double y, double k)
        => (x - 16 * k, y - 58 * k, x + 16 * k, y - 20 * k);
}
