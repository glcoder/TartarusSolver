using System.Diagnostics;
using Tartarus.Rules;
using Tartarus.Solver;

/// <summary>
/// Training data for the playouts' policy network: runs the search plays, and at each decision with a choice every
/// legal action's after-state (played on a copy, the same dice for all, as the heuristic policy compares them) with
/// the search's mean utility for it. File: "TPD1" magic, feature count, row count patched at the end, then one row per
/// action: the after-state's features (float16), the search's mean and standard error (float32), its playouts
/// (int32), the decision's number (int32, shared by its actions), the action's kind, flags (1 the heuristic policy's
/// choice, 2 the search's), the day, a pad byte. With <c>valuesOut</c> the same runs' mornings go there too, as
/// <see cref="SelfPlay"/> writes them (one strong search's runs feed both networks).
/// </summary>
internal static class PolicyData
{
    public static void Run(Engine engine, Func<ulong, SearchPolicy> search, EvalWeights weights, int runs, ulong seed0, string outPath, string? valuesOut = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using var file = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        using var w = new BinaryWriter(file);
        w.Write(0x31445054); // "TPD1"
        w.Write(Features.Count);
        w.Write(0); // rows, patched below
        using var vfile = valuesOut != null ? new FileStream(valuesOut, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20) : null;
        using var vw = vfile != null ? new BinaryWriter(vfile) : null;
        if (vw != null)
        {
            vw.Write(0x32415654); // "TVA2"
            vw.Write(Features.Count);
            vw.Write(SelfPlay.Targets);
            vw.Write(0); // records, patched below
        }
        int records = 0;
        var gate = new object();
        int rows = 0, decisions = 0, done = 0;
        double utility = 0;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, runs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, r =>
        {
            ulong seed = seed0 * 1_000_003 + (ulong)r;
            var s = engine.NewRun(seed);
            var p = search(seed);
            var heuristic = new HeuristicPolicy(engine, weights, seed ^ 0x5DEECE66DUL);
            var dice = new Rng(seed ^ 0xA5A5A5A5UL);
            var found = new List<(Half[] X, ActionValue V, ActionKind Kind, byte Flags, byte Day, int Decision)>();
            Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
            Span<float> x = stackalloc float[Features.Count];
            int decision = 0;
            var mornings = new List<(float[] X, int Days, float Value)>();
            int lastDay = 0;
            while (!s.Ended)
            {
                bool morning = s.Phase == Phase.Move && s.PendingCount == 0 && s.Day != lastDay;
                float[]? mx = null;
                if (morning && vw != null)
                {
                    lastDay = s.Day;
                    mx = new float[Features.Count];
                    Features.Encode(s, mx);
                }
                int k = engine.Legal(s, legal);
                var action = p.Choose(engine, s, legal[..k]);
                if (mx != null) mornings.Add((mx, s.RemainingDays, p.LastValue is double mv ? (float)mv : float.NaN));
                if (p.LastResult is { } result)
                {
                    var mine = heuristic.Choose(engine, s, legal[..k]);
                    var roll = new Rng(dice.Next());
                    foreach (var v in result.Actions)
                    {
                        var copy = s;
                        copy.Rng = roll;
                        engine.Apply(ref copy, v.Action);
                        Features.Encode(copy, x);
                        var h = new Half[Features.Count];
                        for (int i = 0; i < h.Length; i++) h[i] = (Half)x[i];
                        byte flags = (byte)((v.Action == mine ? 1 : 0) | (v.Action == action ? 2 : 0));
                        found.Add((h, v, v.Action.Kind, flags, (byte)s.Day, decision));
                    }
                    decision++;
                }
                engine.Apply(ref s, action);
            }
            var score = RunScore.Of(s);
            lock (gate)
            {
                foreach (var (h, v, kind, flags, day, d) in found)
                {
                    foreach (var f in h) w.Write(f);
                    w.Write((float)v.Mean);
                    w.Write((float)v.StdError);
                    w.Write(v.Playouts);
                    w.Write(decisions + d);
                    w.Write((byte)kind);
                    w.Write(flags);
                    w.Write(day);
                    w.Write((byte)0);
                }
                rows += found.Count;
                if (vw != null)
                {
                    foreach (var (mx, days, value) in mornings)
                    {
                        foreach (float f in mx) vw.Write(f);
                        vw.Write((float)score.Utility);
                        vw.Write((float)score.Chromatic);
                        vw.Write((float)score.Ultimate);
                        vw.Write((float)score.Legendary);
                        vw.Write((float)days);
                        vw.Write(value);
                    }
                    records += mornings.Count;
                }
                decisions += decision;
                utility += score.Utility;
                if (++done % 100 == 0) Console.WriteLine($"{done}/{runs} runs, {decisions} decisions, {rows} actions, utility {utility / done:0.000}, {sw.Elapsed.TotalSeconds:0} s");
            }
        });
        w.Flush();
        file.Seek(8, SeekOrigin.Begin);
        w.Write(rows);
        if (vw != null)
        {
            vw.Flush();
            vfile!.Seek(12, SeekOrigin.Begin);
            vw.Write(records);
            Console.WriteLine($"{records} mornings -> {valuesOut}");
        }
        Console.WriteLine($"{runs} runs, {decisions} decisions, {rows} actions -> {outPath} ({sw.Elapsed.TotalSeconds:0} s, utility {utility / runs:0.000})");
    }
}
