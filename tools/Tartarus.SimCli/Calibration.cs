using System.Diagnostics;
using Tartarus.Rules;
using Tartarus.Solver;

/// <summary>
/// tartarus-sim objcheck: is the reward of a done Objective worth what the search expected of it? Runs the search
/// plays; at the decision that does the Objective (the next one is its Torture Instrument pick) the search's value is
/// compared with its value on the pick, the offer seen. Any decision's best mean is a little high (the best of noisy
/// means), so the same step between any two decisions in a row is the control. The runs' results against the values
/// say whether the search's values hold on average.
/// </summary>
internal static class Calibration
{
    public static void Run(Engine engine, Func<ulong, SearchPolicy> search, int runs, ulong seed0)
    {
        var gate = new object();
        var objective = new List<(double Before, double Pick, double Final, ObjectiveKind Kind, bool Reroll)>();
        var steps = new List<double>();
        var final = new List<(double Value, double Final)>();
        int done = 0;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, runs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, r =>
        {
            ulong seed = seed0 * 1_000_003 + (ulong)r;
            var s = engine.NewRun(seed);
            var p = search(seed);
            Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
            var values = new List<(double Value, bool ObjectivePick, bool Reroll)>();
            bool seen = false;
            while (!s.Ended)
            {
                int k = engine.Legal(s, legal);
                // The Objective's pick, first offer (a Re-roll's offer is the same pick's).
                bool pick = !seen && s.PendingCount > 0 && s.Pending[0].Kind == PendingKind.Instrument && s.Pending[0].ForObjective;
                seen |= pick;
                var action = p.Choose(engine, s, legal[..k]);
                if (p.LastValue is { } v) values.Add((v, pick, s.Rerolls > 0));
                engine.Apply(ref s, action);
            }
            double result = RunScore.Of(s).Utility;
            var kind = s.Objective;
            lock (gate)
            {
                for (int i = 1; i < values.Count; i++)
                {
                    if (values[i].ObjectivePick) objective.Add((values[i - 1].Value, values[i].Value, result, kind, values[i].Reroll));
                    else steps.Add(values[i].Value - values[i - 1].Value);
                }
                foreach (var (v, _, _) in values) final.Add((v, result));
                if (++done % 200 == 0) Console.WriteLine($"{done}/{runs} runs, {objective.Count} Objectives done, {sw.Elapsed.TotalSeconds:0} s");
            }
        });

        Console.WriteLine($"{runs} runs in {sw.Elapsed.TotalSeconds:0} s");
        var (cm, cse) = MeanSe(steps);
        Console.WriteLine($"any decision to the next (control): {cm:+0.000;-0.000} ± {cse:0.000} ({steps.Count} steps)");
        Report("Objective done -> its pick", objective.Select(o => o.Pick - o.Before).ToList(), cm, cse);
        Report("  with a Re-roll left", objective.Where(o => o.Reroll).Select(o => o.Pick - o.Before).ToList(), cm, cse);
        Report("  without", objective.Where(o => !o.Reroll).Select(o => o.Pick - o.Before).ToList(), cm, cse);
        foreach (var kind in objective.Select(o => o.Kind).Distinct().OrderBy(k => k))
            Report($"  {kind}", objective.Where(o => o.Kind == kind).Select(o => o.Pick - o.Before).ToList(), cm, cse);
        var (fm, fse) = MeanSe(final.Select(f => f.Final - f.Value).ToList());
        Console.WriteLine($"run's result - value, every decision: {fm:+0.000;-0.000} ± {fse:0.000}");
        var (om, ose) = MeanSe(objective.Select(o => o.Final - o.Before).ToList());
        Console.WriteLine($"run's result - value before the Objective's pick: {om:+0.000;-0.000} ± {ose:0.000}");
        var (pm, pse) = MeanSe(objective.Select(o => o.Final - o.Pick).ToList());
        Console.WriteLine($"run's result - value on the pick: {pm:+0.000;-0.000} ± {pse:0.000}");
    }

    private static void Report(string name, List<double> d, double control, double controlSe)
    {
        if (d.Count < 2) return;
        var (m, se) = MeanSe(d);
        double z = (m - control) / Math.Sqrt(se * se + controlSe * controlSe);
        Console.WriteLine($"{name}: {m:+0.000;-0.000} ± {se:0.000} ({d.Count}), against the control {m - control:+0.000;-0.000} (z {z:+0.0;-0.0})");
    }

    private static (double Mean, double Se) MeanSe(IReadOnlyList<double> d)
    {
        if (d.Count < 2) return (d.Count == 1 ? d[0] : 0, 0);
        double mean = d.Average();
        double var = d.Sum(x => (x - mean) * (x - mean)) / (d.Count - 1);
        return (mean, Math.Sqrt(var / d.Count));
    }
}
