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

    /// <summary>
    /// tartarus-sim calib: are the chests a hint shows (the best move's expected Chromatic and Ultimate at the Riot)
    /// what the runs end with? Runs the search plays; at each decision its shown expectation beside the run's result,
    /// by Remaining Days and by the expectation's size. Writes every decision to <paramref name="csv"/> when given. With
    /// <paramref name="table"/>, the fit that corrects them (<see cref="ChestCalibration"/>, models/solver/chests.txt).
    /// </summary>
    public static void Chests(Engine engine, Func<ulong, SearchPolicy> search, int runs, ulong seed0, string? csv, string? table = null, string about = "")
    {
        var gate = new object();
        var rows = new List<(int Days, double Chromatic, double Ultimate, int FinalChromatic, int FinalUltimate)>();
        int done = 0;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, runs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, r =>
        {
            ulong seed = seed0 * 1_000_003 + (ulong)r;
            var s = engine.NewRun(seed);
            var p = search(seed);
            Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
            var shown = new List<(int Days, double Chromatic, double Ultimate)>();
            while (!s.Ended)
            {
                int k = engine.Legal(s, legal);
                int days = s.RemainingDays;
                var action = p.Choose(engine, s, legal[..k]);
                if (p.LastResult?.Best is { } best) shown.Add((days, best.Chromatic, best.Ultimate));
                engine.Apply(ref s, action);
            }
            var score = RunScore.Of(s);
            lock (gate)
            {
                foreach (var (d, c, u) in shown) rows.Add((d, c, u, score.Chromatic, score.Ultimate));
                if (++done % 200 == 0) Console.WriteLine($"{done}/{runs} runs, {sw.Elapsed.TotalSeconds:0} s");
            }
        });

        Console.WriteLine($"{runs} runs in {sw.Elapsed.TotalSeconds:0} s, {rows.Count} decisions");
        Console.WriteLine("Remaining Days: decisions, shown Chromatic -> run's, bias ± se, rmse | shown Ultimate -> run's, bias ± se, rmse");
        foreach (var g in rows.GroupBy(x => Math.Min(x.Days, 21)).OrderByDescending(g => g.Key))
            Line(g.Key == 21 ? ">20" : g.Key.ToString(), g.ToList());
        Line("all", rows);
        Console.WriteLine("By the shown Chromatic: decisions, shown -> run's, bias ± se");
        foreach (var g in rows.GroupBy(x => Math.Min(9, (int)x.Chromatic)).OrderBy(g => g.Key))
        {
            var (m, se) = MeanSe(g.Select(x => x.FinalChromatic - x.Chromatic).ToList());
            Console.WriteLine($"  {g.Key}-{g.Key + 1}: {g.Count(),7}  {g.Average(x => x.Chromatic):0.00} -> {g.Average(x => x.FinalChromatic):0.00}  {m:+0.00;-0.00} ± {se:0.00}");
        }
        if (table != null)
        {
            File.WriteAllText(table, ChestCalibration.Fit(rows, about));
            Console.WriteLine($"saved {table}");
        }
        if (csv != null)
            File.WriteAllLines(csv, rows.Select(x => FormattableString.Invariant($"{x.Days},{x.Chromatic:0.000},{x.Ultimate:0.000},{x.FinalChromatic},{x.FinalUltimate}")).Prepend("days,chromatic,ultimate,final_chromatic,final_ultimate"));

        static void Line(string name, List<(int Days, double Chromatic, double Ultimate, int FinalChromatic, int FinalUltimate)> g)
        {
            var (cm, cse) = MeanSe(g.Select(x => x.FinalChromatic - x.Chromatic).ToList());
            var (um, use) = MeanSe(g.Select(x => x.FinalUltimate - x.Ultimate).ToList());
            double crmse = Math.Sqrt(g.Average(x => (x.FinalChromatic - x.Chromatic) * (x.FinalChromatic - x.Chromatic)));
            double urmse = Math.Sqrt(g.Average(x => (x.FinalUltimate - x.Ultimate) * (x.FinalUltimate - x.Ultimate)));
            Console.WriteLine($"  {name,4}: {g.Count,7}  {g.Average(x => x.Chromatic):0.00} -> {g.Average(x => x.FinalChromatic):0.00}  {cm:+0.00;-0.00} ± {cse:0.00}  {crmse:0.00}"
                + $" | {g.Average(x => x.Ultimate):0.00} -> {g.Average(x => x.FinalUltimate):0.00}  {um:+0.00;-0.00} ± {use:0.00}  {urmse:0.00}");
        }
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
