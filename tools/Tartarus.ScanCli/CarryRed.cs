using System.Text.Json;
using Tartarus.Vision;

/// <summary>
/// tartarus-scan carry-red &lt;dataset dir&gt; [--dry]: labels the Consume pick's red frames from the plain frames around
/// them, so the cell network learns to read an Inmate under the red and the target's jaws. During the pick only the
/// eater (the Warden's cell) and its meals change, so a cell is what the plain frame before read (the last with the day
/// counter, the same Warden) or, failing that, what the plain frame after read (the first, the same Warden):
/// <list type="bullet">
/// <item>both agree: that;</item>
/// <item>the frame before has the same Warden: its reading (a meal is Unopened after, still an Inmate here);</item>
/// <item>a cell opened since (Avenger's Casket) and left uneaten: the frame after's reading;</item>
/// <item>the eater: the frame before's reading, if the red frame reads it so too (a Chain Consume raised it otherwise).</item>
/// </list>
/// A label must be the kind of cell the red frame shows (an Inmate, or hidden; or not an Inmate). Carried cells go to
/// meta's CarriedCells, which training trusts; hand-checked cells are left alone. The Consumable flag (the small mouth
/// over an Inmate that has a target, shown on the red screen as on the plain one) follows from the carried Sandbox by
/// the rules, with the instruments the plain frames read.
/// </summary>
internal static class CarryRed
{
    private const double MaxGapSeconds = 30;

    private sealed record Plain(string Id, DateTimeOffset At, EditableSandbox Labels);

    public static int Run(string[] args, Func<string[], ISandboxScanner> makeScanner)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: tartarus-scan carry-red <dataset dir> [--dry]");
            return 1;
        }
        bool dry = args.Contains("--dry");
        var ds = new Dataset(args[0]);
        var truth = Dataset.LoadTruth(ds.TruthPath);
        using var scanner = makeScanner(args);
        var engine = new Tartarus.Rules.Engine();

        // The frames in time order: plain ones (the day counter read) and the red ones (the pick's banner).
        var frames = new List<(string File, string Id, SampleMeta Meta, Plain? Plain, EditableSandbox? Red)>();
        foreach (var (file, labels) in truth.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            string scanPath = Path.Combine(ds.Root, "scans", id + ".json");
            if (ds.MetaOf(id) is not { } meta || !File.Exists(scanPath)) continue;
            var saved = ScanJson.Parse(File.ReadAllText(scanPath), "file");
            if (!saved.Ok) continue;
            if (saved.Days != null)
            {
                frames.Add((file, id, meta, new Plain(id, meta.CapturedAt, EditableSandbox.FromLabels(labels)), null));
                continue;
            }
            var scan = scanner.Scan(ImageIO.Load(Path.Combine(ds.FramesDir, file)));
            if (scan.Text?.ConsumePick != true) continue;
            // Labeled afresh: an earlier run's carried cells count no more unless carried again.
            if (!dry && meta.CarriedCells != null)
            {
                meta.CarriedCells = null;
                File.WriteAllText(Path.Combine(ds.Root, "meta", id + ".json"), JsonSerializer.Serialize(meta, VisionJson.Default.SampleMeta));
            }
            frames.Add((file, id, meta, null, EditableSandbox.FromScan(scan)));
        }

        int red = 0, labeled = 0, cellsCarried = 0, cellsSkipped = 0;
        var skips = new Dictionary<string, int>();
        for (int i = 0; i < frames.Count; i++)
        {
            var (file, id, meta, _, seen) = frames[i];
            if (seen == null) continue;
            red++;
            var at = meta.CapturedAt;
            var before = frames.Take(i).Select(f => f.Plain).LastOrDefault(p => p != null);
            var after = frames.Skip(i + 1).Select(f => f.Plain).FirstOrDefault(p => p != null);
            if (before != null && (at - before.At).TotalSeconds > MaxGapSeconds) before = null;
            if (after != null && (after.At - at).TotalSeconds > MaxGapSeconds) after = null;
            int warden = seen.Warden > 1 ? seen.Warden : before?.Labels.Warden ?? 0;
            if (before != null && before.Labels.Warden != warden) before = null;
            if (after != null && after.Labels.Warden != warden) after = null;
            string? why = warden < 2 ? "no Warden" : before == null && after == null ? "no plain frame of this Warden around" : null;
            if (why != null)
            {
                skips[why] = skips.GetValueOrDefault(why) + 1;
                continue;
            }

            var result = EditableSandbox.FromLabels(truth[file]);
            var carried = new List<int>();
            for (int n = 2; n <= 16; n++)
            {
                if (meta.EditedCells.Contains(n)) continue;
                var label = Carry(n, warden, before?.Labels, after?.Labels, seen[n]);
                if (label == null || !SameKind(label, seen[n]))
                {
                    cellsSkipped++;
                    continue;
                }
                var c = result[n];
                (c.Kind, c.Code, c.Rarity, c.Nest, c.Incident, c.VisibleRarity, c.ScratchMarks, c.Tusk, c.Prey) =
                    (label.Kind, label.Code, label.Rarity, label.Nest, label.Incident, label.VisibleRarity, label.ScratchMarks, label.Tusk, label.Prey);
                (c.Consumable, c.Covered) = (false, false);
                carried.Add(n);
            }
            result.Warden = warden;
            if (result.Instruments.Count == 0) result.Instruments = [.. (before ?? after)!.Labels.Instruments];
            var state = engine.FromObservation(GameObservation.From(result), 1);
            foreach (int n in carried)
                if (result[n].Kind == CellKind.Inmate) result[n].Consumable = Tartarus.Rules.Engine.HasTarget(state, n - 1);
            labeled++;
            cellsCarried += carried.Count;
            Console.WriteLine($"{id}: before {before?.Id ?? "-"}, after {after?.Id ?? "-"}, Warden {warden}, {carried.Count} cells");
            if (dry) continue;
            ds.SetLabels(file, result.ToLabels());
            meta.CarriedCells = carried;
            File.WriteAllText(Path.Combine(ds.Root, "meta", id + ".json"), JsonSerializer.Serialize(meta, VisionJson.Default.SampleMeta));
        }
        Console.WriteLine($"{red} red frames, {labeled} labeled: {cellsCarried} cells carried, {cellsSkipped} left out{(dry ? " (dry run)" : "")}");
        foreach (var (why, count) in skips.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  skipped {count}: {why}");
        return 0;
    }

    private static CellLabel? Carry(int n, int warden, EditableSandbox? before, EditableSandbox? after, CellLabel seen)
    {
        var (b, a) = (before?[n], after?[n]);
        if (n == warden)
            return b is { Kind: CellKind.Inmate } && seen.Kind == CellKind.Inmate && seen.Code == b.Code && seen.Rarity == b.Rarity ? b : null;
        if (b != null && a != null && Same(b, a)) return b;
        if (b != null && (b.Kind == CellKind.Inmate || seen.Kind != CellKind.Inmate && !seen.Covered)) return b;
        if (a is { Kind: CellKind.Inmate } && (b == null || b.Kind != CellKind.Inmate)) return a;
        return null;
    }

    private static bool Same(CellLabel x, CellLabel y) =>
        x.Kind == y.Kind && (x.Kind != CellKind.Inmate || (x.Code == y.Code && x.Rarity == y.Rarity));

    /// <summary>The red frame shows the same kind of cell: an Inmate stays one (or is hidden), a closed cell stays closed.</summary>
    private static bool SameKind(CellLabel label, CellLabel red) =>
        label.Kind == CellKind.Inmate ? red.Kind == CellKind.Inmate || red.Covered : red.Kind != CellKind.Inmate;
}
