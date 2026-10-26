using Tartarus.Rules;
using Tartarus.Solver;

/// <summary>Measuring and tuning the evaluation weights by playing runs on the same seeds (paired comparisons).</summary>
internal static class Tuning
{
    /// <summary>Utilities of <paramref name="runs"/> heuristic runs; <paramref name="prepare"/> may change each new run first.</summary>
    public static double[] Utilities(Engine engine, EvalWeights w, int runs, ulong seed0, Func<GameState, GameState>? prepare = null)
    {
        var u = new double[runs];
        Parallel.For(0, runs, r =>
        {
            ulong seed = seed0 * 1_000_003 + (ulong)r;
            var s = engine.NewRun(seed);
            if (prepare != null) s = prepare(s);
            Runs.PlayOut(engine, ref s, new HeuristicPolicy(engine, w, seed));
            u[r] = RunScore.Of(s).Utility;
        });
        return u;
    }

    public static (double Mean, double StdErr) Paired(double[] a, double[] b)
    {
        var d = a.Zip(b, (x, y) => x - y).ToArray();
        double mean = d.Average();
        double sd = Math.Sqrt(d.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, d.Length - 1));
        return (mean, sd / Math.Sqrt(d.Length));
    }

    public static void Save(EvalWeights w, string? path)
    {
        path ??= Path.Combine("models", "solver", "weights.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, WeightsFile.Write(w));
        Console.WriteLine($"saved {path}");
    }

    /// <summary>Each instrument's worth: runs that take it at the start against runs that take none, on the same seeds.</summary>
    public static EvalWeights Instruments(Engine engine, EvalWeights w, int runs)
    {
        var none = Utilities(engine, w, runs, 77, s =>
        {
            s.PendingCount = 0;
            return s;
        });
        Console.WriteLine($"no instrument at the start: utility {none.Average():0.0000}");
        var values = (double[])w.InstrumentValue.Clone();
        for (int i = 0; i < GameData.InstrumentCount; i++)
        {
            var inst = (Instrument)i;
            var with = Utilities(engine, w, runs, 77, s =>
            {
                s.Pending[0].A = (byte)inst;
                s.Pending[0].Count = 1;
                engine.Apply(ref s, new GameAction(ActionKind.PickInstrument, (byte)inst));
                return s;
            });
            var (mean, se) = Paired(with, none);
            values[i] = Math.Round(mean, 4);
            Console.WriteLine($"{GameData.Info(inst).Name,-46} {mean,8:+0.0000;-0.0000} ± {se:0.0000}");
        }
        return w with { InstrumentValue = values };
    }

    /// <summary>Coordinate search: each weight scaled down and up in turn, kept when paired runs say it's better by 2 standard errors.</summary>
    public static EvalWeights Tune(Engine engine, EvalWeights w, int runs, int passes, string? outPath)
    {
        ulong seed = 101;
        var current = Utilities(engine, w, runs, seed);
        Console.WriteLine($"start: utility {current.Average():0.0000}");
        var knobs = new (string Name, Func<EvalWeights, double> Get, Func<EvalWeights, double, EvalWeights> Set)[]
        {
            ("potential.normal", x => x.Potential[0], (x, v) => x with { Potential = With(x.Potential, 0, v) }),
            ("potential.magic", x => x.Potential[1], (x, v) => x with { Potential = With(x.Potential, 1, v) }),
            ("potential.rare", x => x.Potential[2], (x, v) => x with { Potential = With(x.Potential, 2, v) }),
            ("potential.legendary", x => x.Potential[3], (x, v) => x with { Potential = With(x.Potential, 3, v) }),
            ("potential.ultimate", x => x.Potential[4], (x, v) => x with { Potential = With(x.Potential, 4, v) }),
            ("horizon", x => x.Horizon, (x, v) => x with { Horizon = v }),
            ("merge_ready", x => x.MergeReady, (x, v) => x with { MergeReady = v }),
            ("unopened", x => x.Unopened, (x, v) => x with { Unopened = v }),
            ("marker", x => x.Marker, (x, v) => x with { Marker = v }),
            ("objective", x => x.Objective, (x, v) => x with { Objective = v }),
            ("reroll_below", x => x.RerollBelow, (x, v) => x with { RerollBelow = v }),
        };
        for (int pass = 0; pass < passes; pass++)
        {
            foreach (var (name, get, set) in knobs)
                foreach (double factor in new[] { 0.6, 1.6 })
                {
                    var candidate = set(w, get(w) * factor);
                    var u = Utilities(engine, candidate, runs, seed);
                    var (mean, se) = Paired(u, current);
                    bool keep = mean > 2 * se;
                    Console.WriteLine($"pass {pass + 1} {name} x{factor}: {get(w):0.####} -> {get(candidate):0.####}  {mean:+0.0000;-0.0000} ± {se:0.0000}{(keep ? "  kept" : "")}");
                    if (!keep) continue;
                    w = candidate;
                    current = u;
                    break;
                }
            seed++;
            current = Utilities(engine, w, runs, seed);
            Console.WriteLine($"after pass {pass + 1}: utility {current.Average():0.0000}");
            Save(w, outPath);
        }
        return w;

        static double[] With(double[] a, int i, double v)
        {
            var b = (double[])a.Clone();
            b[i] = v;
            return b;
        }
    }
}
