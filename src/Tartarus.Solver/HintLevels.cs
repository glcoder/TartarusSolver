using System.Globalization;

namespace Tartarus.Solver;

/// <summary>A hint quality level: playouts per action, and what it costs and gives, as measured.</summary>
public sealed record HintLevel(int Playouts, double Milliseconds, double Chromatic);

/// <summary>
/// The hint quality levels, from models/solver/levels.txt ("playouts=N ms=T chromatic=C" per line; tartarus-sim levels
/// measures them). Below the lowest the runs get much worse; above the highest they stop getting better.
/// </summary>
public static class HintLevels
{
    public static IReadOnlyList<HintLevel> Default { get; } =
    [
        new(32, 0.5, 6.15), new(64, 0.8, 6.97), new(128, 1.3, 7.02), new(256, 2.4, 7.13),
        new(512, 3.9, 7.26), new(1024, 7.7, 7.31), new(2048, 13.9, 7.3),
    ];

    public static string? Find() => AppFiles.Find("models", "solver", "levels.txt");

    public static IReadOnlyList<HintLevel> Load(string? path = null)
    {
        path ??= Find();
        if (path == null) return Default;
        var levels = new List<HintLevel>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int playouts = 0;
            double ms = 0, chromatic = 0;
            foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length != 2) continue;
                switch (kv[0])
                {
                    case "playouts": playouts = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
                    case "ms": ms = double.Parse(kv[1], CultureInfo.InvariantCulture); break;
                    case "chromatic": chromatic = double.Parse(kv[1], CultureInfo.InvariantCulture); break;
                }
            }
            if (playouts > 0) levels.Add(new HintLevel(playouts, ms, chromatic));
        }
        return levels.Count > 0 ? levels.OrderBy(l => l.Playouts).ToList() : Default;
    }

    public static string Write(IEnumerable<HintLevel> levels) =>
        "# Move hint quality levels: playouts per action, median time of a hint (this PC, half the cores), Chromatic per run\n"
        + "# (runs on the same seeds, search with the value and policy networks). Remeasure: tartarus-sim levels\n"
        + string.Concat(levels.Select(l => string.Create(CultureInfo.InvariantCulture, $"playouts={l.Playouts} ms={l.Milliseconds:0.0} chromatic={l.Chromatic:0.00}\n")));
}
