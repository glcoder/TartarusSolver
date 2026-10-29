using System.Diagnostics;
using Tartarus.Rules;
using Tartarus.Solver;

/// <summary>
/// tartarus-sim obscheck: does a state made of what the screen shows (as the app makes it, <see cref="Engine.FromObservation"/>)
/// get the values the engine's own state gets? Runs the search plays; at the Objective's pick, the decision before it and
/// every 8th other decision, a second search weighs the state rebuilt from <see cref="Observe"/>. The app's values fell on
/// the Objective's pick (2026-10-29: -0.44 ± 0.22 Chromatic over 22 real picks) where the engine's rise.
/// </summary>
internal static class ObservedCheck
{
    public static void Run(Engine engine, Func<ulong, SearchPolicy> search, int runs, ulong seed0)
    {
        var gate = new object();
        var rows = new List<(string Kind, double True, double Seen)>();
        var jumps = new List<(double True, double Seen)>();
        int done = 0;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, runs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, r =>
        {
            ulong seed = seed0 * 1_000_003 + (ulong)r;
            var s = engine.NewRun(seed);
            var p = search(seed);
            var q = search(seed ^ 0x9E3779B97F4A7C15UL);
            Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
            var mine = new List<(string, double, double)>();
            var myJumps = new List<(double, double)>();
            GameState? before = null;
            double beforeTrue = 0;
            bool seen = false;
            int step = 0;
            while (!s.Ended)
            {
                int k = engine.Legal(s, legal);
                bool pick = !seen && s.PendingCount > 0 && s.Pending[0].Kind == PendingKind.Instrument && s.Pending[0].ForObjective;
                seen |= pick;
                var action = p.Choose(engine, s, legal[..k]);
                double value = p.LastValue ?? 0;
                bool started = s.Instruments != 0;
                if (pick && before is { } b)
                {
                    double bSeen = Seen(engine, q, b, seed + 17);
                    double pSeen = Seen(engine, q, s, seed + 29);
                    mine.Add(("before the pick", beforeTrue, bSeen));
                    mine.Add(("Objective's pick", value, pSeen));
                    myJumps.Add((value - beforeTrue, pSeen - bSeen));
                }
                else if (started && step % 8 == 3 && !pick) mine.Add(("control", value, Seen(engine, q, s, seed + (ulong)step)));
                if (started) (before, beforeTrue) = (s, value);
                engine.Apply(ref s, action);
                step++;
            }
            lock (gate)
            {
                rows.AddRange(mine);
                jumps.AddRange(myJumps);
                if (++done % 100 == 0) Console.WriteLine($"{done}/{runs} runs, {jumps.Count} Objective picks, {sw.Elapsed.TotalSeconds:0} s");
            }
        });
        Console.WriteLine($"{runs} runs in {sw.Elapsed.TotalSeconds:0} s");
        foreach (var g in rows.GroupBy(x => x.Kind))
        {
            var (m, se) = MeanSe(g.Select(x => x.Seen - x.True).ToList());
            Console.WriteLine($"{g.Key}: seen - true {m:+0.000;-0.000} ± {se:0.000} ({g.Count()})");
        }
        var (tm, tse) = MeanSe(jumps.Select(j => j.True).ToList());
        var (sm, sse) = MeanSe(jumps.Select(j => j.Seen).ToList());
        Console.WriteLine($"Objective done -> its pick: true states {tm:+0.000;-0.000} ± {tse:0.000}, seen states {sm:+0.000;-0.000} ± {sse:0.000}");
    }

    /// <summary>
    /// tartarus-sim opened: the rarities of Inmates found in cells that were Unopened (no rarity shown) one decision
    /// before, in runs the search plays: what the app's log can be held against (instruments and incidents included).
    /// </summary>
    public static void Opened(Engine engine, Func<ulong, SearchPolicy> search, int runs, ulong seed0)
    {
        var counts = new long[(int)Rarity.Chromatic + 1];
        var gate = new object();
        Parallel.For(0, runs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, r =>
        {
            ulong seed = seed0 * 1_000_003 + (ulong)r;
            var s = engine.NewRun(seed);
            var p = search(seed);
            Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
            var mine = new long[counts.Length];
            while (!s.Ended)
            {
                int k = engine.Legal(s, legal);
                var action = p.Choose(engine, s, legal[..k]);
                var before = s;
                engine.Apply(ref s, action);
                for (int n = 1; n < GameData.Cells; n++)
                {
                    var b = before.Cells[n];
                    if (b.Sealed && !b.Has(CellMarks.RarityShown) && s.Cells[n].HasInmate) mine[(int)s.Cells[n].Inmate.Rarity]++;
                }
            }
            lock (gate) for (int i = 0; i < counts.Length; i++) counts[i] += mine[i];
        });
        long total = counts.Sum();
        Console.WriteLine($"{runs} runs, {total} Inmates found behind Unopened cells: "
            + string.Join("  ", Enum.GetValues<Rarity>().Where(x => (int)x < counts.Length).Select(x => $"{x} {100.0 * counts[(int)x] / total:0.0}%")));
    }

    private static double Seen(Engine engine, SearchPolicy q, in GameState s, ulong seed)
    {
        var rebuilt = engine.FromObservation(Observe(s), seed);
        Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
        int k = engine.Legal(rebuilt, legal);
        if (k == 0) return double.NaN;
        q.Choose(engine, rebuilt, legal[..k]);
        return q.LastValue ?? double.NaN;
    }

    /// <summary>What the screen shows of a state: the Sandbox, the panels, the front choice waiting.</summary>
    public static Observation Observe(in GameState s)
    {
        var o = new Observation
        {
            Warden = s.Position,
            RemainingDays = s.RemainingDays,
            Chromatic = s.Chromatic,
            Theme = s.Theme,
            Objective = s.Objective,
            ObjectiveProgress = s.ObjectiveProgress,
            ObjectiveDone = s.ObjectiveDone,
            Rerolls = s.Rerolls,
        };
        for (int i = 0; i < GameData.InstrumentCount; i++)
            if (s.Has((Instrument)i)) o.Instruments.Add((Instrument)i);
        for (int n = 1; n < GameData.Cells; n++)
        {
            var c = s.Cells[n];
            ref var oc = ref o.Cells[n];
            oc.Marker = c.Marker != Incident.None;
            if (c.HasInmate)
            {
                oc.Kind = ObservedKind.Inmate;
                (oc.Code, oc.Rarity) = (c.Inmate.Code, c.Inmate.Rarity);
                oc.Tusk = c.Has(CellMarks.Tusk);
                for (int j = 0; j < s.PreyCount; j++) if (s.Prey[j].Prey == c.Inmate.Id) oc.Prey = true;
            }
            else
            {
                oc.Kind = c.Sealed ? ObservedKind.Unopened : ObservedKind.Empty;
                if (c.Sealed && c.Has(CellMarks.RarityShown) && !c.Inmate.IsNone) oc.Shown = c.Inmate.Rarity;
                oc.Scratch = c.Has(CellMarks.Scratch);
                oc.Nest = c.NestDue > 0;
            }
        }
        if (s.Phase == Phase.Move)
            for (int i = 0; i < s.DestinationCount; i++) o.Destinations.Add(s.Destinations[i]);
        else
        {
            o.DecisionUsed = s.Used != DayAction.None;
            o.ConsumedToday = s.Used == DayAction.Consume;
            o.ReplaceUsed = s.Replaced;
        }
        if (s.PendingCount > 0)
        {
            var p = s.Pending[0];
            switch (p.Kind)
            {
                case PendingKind.Instrument:
                    for (int i = 0; i < p.Count; i++) o.Offer.Add(p.Offer(i));
                    o.OfferForObjective = p.ForObjective || o.Instruments.Count > 0;
                    break;
                case PendingKind.Treasure:
                    for (int t = 0; t < 4; t++) if ((p.A & (1 << t)) != 0) o.Treasures.Add((Treasure)t);
                    o.TreasurePicks = Math.Max((int)p.Picks, 1);
                    break;
                case PendingKind.Interrogation: o.Interrogation = true; break;
                case PendingKind.Ratway: o.Ratway = true; break;
                case PendingKind.Chain: o.ChainGoesOn = true; break;
            }
        }
        return o;
    }

    private static (double Mean, double Se) MeanSe(IReadOnlyList<double> d)
    {
        var x = d.Where(v => !double.IsNaN(v)).ToList();
        if (x.Count < 2) return (x.Count == 1 ? x[0] : 0, 0);
        double m = x.Average();
        return (m, Math.Sqrt(x.Sum(v => (v - m) * (v - m)) / (x.Count - 1) / x.Count));
    }
}
