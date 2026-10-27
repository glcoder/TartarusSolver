using System.Globalization;

namespace Tartarus.Solver;

/// <summary>
/// What a hint's expected chests become at the Riot: the search's playouts play worse than the player the search
/// advises, so its expectations run low (most early in a run). Per Remaining Days a line, the run's result ≈ a + b ×
/// the expectation, for Chromatic and for Ultimate, fitted on runs the search played (tartarus-sim calib --table);
/// models/solver/chests.txt, "days=N chromatic=a,b ultimate=a,b" per line. Only what's shown: moves rank as searched.
/// </summary>
public sealed class ChestCalibration
{
    private readonly Dictionary<int, (double A, double B)> _chromatic = [], _ultimate = [];

    /// <summary>
    /// Remaining Days at or above this share one line: a run's first days, and an Iron Law run's (30 days) first ten,
    /// which on their own would be a line for runs near the cap only, not for an ordinary run's start.
    /// </summary>
    public const int MaxDays = 19;

    public static string? Find() => AppFiles.Find("models", "solver", "chests.txt");

    public static ChestCalibration? Load(string? path = null)
    {
        path ??= Find();
        if (path == null) return null;
        var c = new ChestCalibration();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int? days = null;
            (double, double)? chromatic = null, ultimate = null;
            foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length != 2) continue;
                switch (kv[0])
                {
                    case "days": days = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
                    case "chromatic": chromatic = Pair(kv[1]); break;
                    case "ultimate": ultimate = Pair(kv[1]); break;
                }
            }
            if (days is not int d) continue;
            if (chromatic is { } ch) c._chromatic[d] = ch;
            if (ultimate is { } u) c._ultimate[d] = u;
        }
        return c._chromatic.Count > 0 ? c : null;

        static (double, double) Pair(string s)
        {
            var ab = s.Split(',');
            return (double.Parse(ab[0], CultureInfo.InvariantCulture), double.Parse(ab[1], CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The expected Chromatic and Ultimate at the Riot for a search's expectation with <paramref name="remainingDays"/> left.</summary>
    public (double Chromatic, double Ultimate) Adjust(int remainingDays, double chromatic, double ultimate)
    {
        int d = Math.Clamp(remainingDays, 0, MaxDays);
        if (_chromatic.TryGetValue(d, out var c)) chromatic = Math.Clamp(c.A + c.B * chromatic, 0, 10);
        if (_ultimate.TryGetValue(d, out var u)) ultimate = Math.Clamp(u.A + u.B * ultimate, 0, 15);
        return (chromatic, ultimate);
    }

    /// <summary>The table for <paramref name="rows"/> (Remaining Days, expected Chromatic and Ultimate, the run's): least squares per Remaining Days.</summary>
    public static string Fit(IEnumerable<(int Days, double Chromatic, double Ultimate, int FinalChromatic, int FinalUltimate)> rows, string about)
    {
        var lines = new List<string>
        {
            "# A hint's expected chests -> what the runs ended with: per Remaining Days (19 = 19 and more), run's result =",
            "# a + b * expectation, for Chromatic and Ultimate. " + about,
        };
        foreach (var g in rows.GroupBy(r => Math.Min(r.Days, MaxDays)).OrderByDescending(g => g.Key))
        {
            var (ca, cb) = Line(g.Select(r => (r.Chromatic, (double)r.FinalChromatic)).ToList());
            var (ua, ub) = Line(g.Select(r => (r.Ultimate, (double)r.FinalUltimate)).ToList());
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"days={g.Key} chromatic={ca:0.000},{cb:0.000} ultimate={ua:0.000},{ub:0.000}"));
        }
        return string.Join("\n", lines) + "\n";

        // Least squares; a flat bias when the expectations hardly vary.
        static (double A, double B) Line(List<(double X, double Y)> p)
        {
            double mx = p.Average(q => q.X), my = p.Average(q => q.Y);
            double sxx = p.Sum(q => (q.X - mx) * (q.X - mx)), sxy = p.Sum(q => (q.X - mx) * (q.Y - my));
            if (p.Count < 50 || sxx < 1e-6 * p.Count) return (my - mx, 1);
            double b = sxy / sxx;
            return (my - b * mx, b);
        }
    }
}
