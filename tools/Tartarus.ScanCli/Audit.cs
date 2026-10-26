using System.Globalization;
using Tartarus.Vision;

/// <summary>
/// tartarus-scan audit &lt;dataset dir&gt;: the rules' odds as the user's own frames show them, beside the simulator's
/// numbers (tartarus-sim stats). Read from consecutive frames of the dataset (the scans' readings, hand corrections
/// included):
/// <list type="bullet">
/// <item>a door's rarity: the Warden lands on an Unopened cell (no shown rarity, Scratch Marks or marker) and the next
/// frame, within a few seconds, shows who is inside;</item>
/// <item>a Replace's rarity: only the Warden's Inmate changed, to another Code (a Consume empties its meal's cell);</item>
/// <item>mornings: how many Destination cards, the Interrogation Room among three when in reach;</item>
/// <item>how often the Sandbox has a Prison Incident marker.</item>
/// </list>
/// Runs with an instrument that bends a count are left out of it (Sorting Hook, Warden's Scales and the Replace
/// instruments for rarities; Felon's Dog Tag and Forged Key for Destinations).
/// </summary>
internal static class Audit
{
    private const double MaxGapSeconds = 5;

    private sealed record Frame(string Id, DateTime At, EditableSandbox L);

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: tartarus-scan audit <dataset dir>");
            return 1;
        }
        var ds = new Dataset(args[0]);
        var frames = new List<Frame>();
        foreach (var (file, labels) in Dataset.LoadTruth(ds.TruthPath).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string id = Path.GetFileNameWithoutExtension(file), scanPath = Path.Combine(ds.Root, "scans", id + ".json");
            if (!File.Exists(scanPath)) continue;
            var scan = ScanJson.Parse(File.ReadAllText(scanPath), "file");
            // Plain Sandbox screens only (the day counter read): no red, fade or banner over the cells.
            if (!scan.Ok || scan.Days == null) continue;
            if (!DateTime.TryParseExact(id, "yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
            frames.Add(new Frame(id, at, EditableSandbox.FromLabels(labels)));
        }
        Console.WriteLine($"{frames.Count} plain Sandbox frames");

        var doors = new int[5];
        var replaces = new int[5];
        int replaceSameCode = 0;
        var seenMornings = new HashSet<(DateTime Run, int Days)>();
        int mornings = 0, threeCards = 0, twoCards = 0, roomReach = 0, roomOffered = 0;
        int markerFrames = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            var (a, la) = (frames[i], frames[i].L);
            if (la.Cells.Skip(1).Any(c => c.Incident)) markerFrames++;

            // A morning (once per day of a run).
            if (la.Phase == Phases.Destination && la.Destinations.Count > 0 && la.Days is int days
                && seenMornings.Add((a.At.Date, days * 1000 + la.Warden)))
            {
                if (!Holds(la, "dogtag", "forged"))
                {
                    mornings++;
                    if (la.Destinations.Count == 3) threeCards++;
                    if (la.Destinations.Count == 2) twoCards++;
                    int steps = (1 - la.Warden + 16) % 16;
                    if (la.Warden > 0 && steps is >= 1 and <= 6 && la.Destinations.Count == 3)
                    {
                        roomReach++;
                        if (la.Destinations.Contains(1)) roomOffered++;
                    }
                }
            }

            if (i + 1 >= frames.Count) continue;
            var (b, lb) = (frames[i + 1], frames[i + 1].L);
            if ((b.At - a.At).TotalSeconds > MaxGapSeconds || la.Days != lb.Days && la.Days - 1 != lb.Days) continue;

            // A door: the Warden goes from its morning to a Destination behind a closed door.
            if (la.Phase == Phases.Destination && lb.Phase == Phases.Decision && lb.Warden > 1 && la.Destinations.Contains(lb.Warden))
            {
                var before = la[lb.Warden];
                var after = lb[lb.Warden];
                if (before.Kind == CellKind.Unopened && before.VisibleRarity == null && !before.ScratchMarks && !before.Incident && !before.Nest
                    && after.Kind == CellKind.Inmate && !Holds(la, "hook", "scales"))
                    doors[(int)after.Rarity]++;
            }

            // A Replace: the same day and Warden, only the Warden's Inmate changed, to another Code.
            if (la.Phase == Phases.Decision && lb.Phase == Phases.Decision && la.Warden == lb.Warden && la.Warden > 1 && la.Days == lb.Days)
            {
                var (x, y) = (la[la.Warden], lb[la.Warden]);
                bool othersSame = Enumerable.Range(2, 15).Where(n => n != la.Warden).All(n => Same(la[n], lb[n]));
                if (x.Kind == CellKind.Inmate && y.Kind == CellKind.Inmate && othersSame && !Same(x, y)
                    && !Holds(la, "noose", "scapegoat", "maiden", "stake", "axe", "pestle", "scales") && !x.Tusk)
                {
                    if (x.Code == y.Code) replaceSameCode++;
                    else replaces[(int)y.Rarity]++;
                }
            }
        }

        Console.WriteLine();
        Report("Door rarity (landing on a plain Unopened cell)", doors, Tartarus.Rules.RuleOptions.Default.DoorWeights);
        Report("Replace rarity (only the Warden's Inmate changed, new Code)", replaces, Tartarus.Rules.RuleOptions.Default.ReplaceWeights);
        Console.WriteLine($"  same Code, other rarity (a misread, or not a Replace): {replaceSameCode}");
        Console.WriteLine();
        Console.WriteLine($"Mornings: {mornings}; 3 Destinations {Pct(threeCards, mornings)}, 2 Destinations {Pct(twoCards, mornings)} (simulator: {100 - Tartarus.Rules.RuleOptions.Default.FewerDestinationsPercent:0}% / {Tartarus.Rules.RuleOptions.Default.FewerDestinationsPercent:0}%)");
        Console.WriteLine($"Interrogation Room among 3 Destinations when in reach: {Pct(roomOffered, roomReach)} of {roomReach} (simulator: tartarus-sim stats)");
        Console.WriteLine($"Frames with a Prison Incident marker: {Pct(markerFrames, frames.Count)} of {frames.Count}");
        return 0;
    }

    private static bool Holds(EditableSandbox l, params string[] ids) => l.Instruments.Any(ids.Contains);

    private static bool Same(CellLabel x, CellLabel y) =>
        x.Kind == y.Kind && (x.Kind != CellKind.Inmate || (x.Code == y.Code && x.Rarity == y.Rarity));

    private static void Report(string title, int[] counts, double[] weights)
    {
        int n = counts.Sum();
        double w = weights.Sum();
        Console.WriteLine($"{title}: {n} seen");
        string[] names = ["Normal", "Magic", "Rare", "Legendary", "Ultimate"];
        for (int r = 0; r < 5; r++)
        {
            double p = n > 0 ? (double)counts[r] / n : 0, expect = weights[r] / w;
            // Binomial standard error of the share, at the simulator's value.
            double se = n > 0 ? Math.Sqrt(expect * (1 - expect) / n) : 0;
            double z = se > 0 ? (p - expect) / se : 0;
            Console.WriteLine($"  {names[r],-9} {counts[r],4}  {100 * p,5:0.0}%   simulator {100 * expect,5:0.0}%   {(Math.Abs(z) >= 2 ? $"off by {z:+0.0;-0.0} SE" : "fits")}");
        }
    }

    private static string Pct(int k, int n) => n > 0 ? $"{100.0 * k / n:0.0}%" : "-";
}
