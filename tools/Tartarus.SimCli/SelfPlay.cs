using System.Diagnostics;
using Tartarus.Rules;
using Tartarus.Solver;

/// <summary>
/// Training data for the value network: runs played by a policy (the search), each morning's state (Select a
/// Destination, nothing waiting) written with how the run ended and what the search made of the morning. File: "TVA2"
/// magic, feature count, target count, record count patched at the end, then per record the features and the targets
/// (utility, Chromatic, Ultimate, Legendary, remaining days, the search's value of the morning: its best Destination's
/// mean utility, NaN when it had no choice or the policy isn't a search), float32 each.
/// </summary>
internal static class SelfPlay
{
    public const int Targets = 6;

    public static void Run(Engine engine, Func<ulong, IPolicy> policy, int runs, ulong seed0, string outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using var file = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(file);
        w.Write(0x32415654); // "TVA2"
        w.Write(Features.Count);
        w.Write(Targets);
        w.Write(0); // records, patched below
        var gate = new object();
        int records = 0, done = 0;
        double utility = 0;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, runs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, r =>
        {
            ulong seed = seed0 * 1_000_003 + (ulong)r;
            var s = engine.NewRun(seed);
            var p = policy(seed);
            var mornings = new List<float[]>();
            var days = new List<int>();
            var values = new List<float>();
            Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
            int lastDay = 0;
            while (!s.Ended)
            {
                bool morning = s.Phase == Phase.Move && s.PendingCount == 0 && s.Day != lastDay;
                if (morning)
                {
                    lastDay = s.Day;
                    var x = new float[Features.Count];
                    Features.Encode(s, x);
                    mornings.Add(x);
                    days.Add(s.RemainingDays);
                }
                int k = engine.Legal(s, legal);
                var action = p.Choose(engine, s, legal[..k]);
                if (morning) values.Add(p is SearchPolicy sp && sp.LastValue is double v ? (float)v : float.NaN);
                engine.Apply(ref s, action);
            }
            var score = RunScore.Of(s);
            lock (gate)
            {
                for (int i = 0; i < mornings.Count; i++)
                {
                    foreach (float v in mornings[i]) w.Write(v);
                    w.Write((float)score.Utility);
                    w.Write((float)score.Chromatic);
                    w.Write((float)score.Ultimate);
                    w.Write((float)score.Legendary);
                    w.Write((float)days[i]);
                    w.Write(values[i]);
                }
                records += mornings.Count;
                utility += score.Utility;
                if (++done % 100 == 0) Console.WriteLine($"{done}/{runs} runs, {records} mornings, utility {utility / done:0.000}, {sw.Elapsed.TotalSeconds:0} s");
            }
        });
        w.Flush();
        file.Seek(12, SeekOrigin.Begin);
        w.Write(records);
        Console.WriteLine($"{runs} runs, {records} mornings -> {outPath} ({sw.Elapsed.TotalSeconds:0} s, utility {utility / runs:0.000})");
    }
}
