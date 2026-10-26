using System.Diagnostics;
using System.Globalization;
using Tartarus.Rules;
using Tartarus.Solver;

// tartarus-sim: runs of the Tartarus rules.
//   tartarus-sim trace [--seed N] [--policy P]                        one run, every decision and the state after it
//   tartarus-sim play [--runs N] [--policy P] [--seed S] [--threads T]   results over many runs
//   tartarus-sim stats [--runs N]                                     the rules' odds against players' boards
//   tartarus-sim bench [--seconds S]                                  random runs per second, one thread
//   tartarus-sim instruments [--runs N] [--out file]                  each instrument's worth: runs that take it at the start
//   tartarus-sim tune [--runs N] [--passes K] [--out file]            the evaluation weights, by paired runs
//   tartarus-sim selfplay [--runs N] [--policy mc:32] [--seed S] [--out file]   value network training data
//   tartarus-sim compare --a P --b P [--net-a file] [--net-b file] [--runs N] [--seed S]   two policies on the same seeds
// Policies P: random, greedy, heuristic, mc:N (Monte Carlo search with N playouts per action at every decision).
// --weights file: evaluation weights (default models/solver/weights.txt when it exists).

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length == 0)
{
    Console.WriteLine("""
        tartarus-sim trace [--seed N] [--policy random|greedy]
        tartarus-sim play [--runs N] [--policy random|greedy] [--seed S] [--threads T]
        tartarus-sim stats [--runs N]
        tartarus-sim bench [--seconds S]
        tartarus-sim instruments [--runs N] [--out file]
        tartarus-sim tune [--runs N] [--passes K] [--out file]
        """);
    return 1;
}
// Long runs give way to the game and the hint app; timings are measured at normal priority.
if (args[0] is not ("latency" or "levels")) Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
var engine = new Engine();
var weights = LoadWeights();
var globalWeights = weights;
var valueNet = Opt("--net") is { } netPath ? ValueNet.Load(netPath) : ValueNet.Find() is { } found ? ValueNet.Load(found) : null;
return args[0] switch
{
    "trace" => Trace(),
    "play" => Play(),
    "stats" => Stats(),
    "bench" => Bench(),
    "instruments" => Instruments(),
    "tune" => Tune(),
    "selfplay" => SelfPlayCommand(),
    "compare" => Compare(),
    "latency" => Latency(),
    "levels" => Levels(),
    _ => Fail($"unknown command {args[0]}"),
};

int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

string? Opt(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
int IntOpt(string name, int fallback) => int.TryParse(Opt(name), out int v) ? v : fallback;

IPolicy MakePolicy(ulong seed, string? spec = null, ValueNet? net = null, EvalWeights? w = null)
{
    string p = spec ?? Opt("--policy") ?? "random";
    net ??= valueNet;
    var weights = w ?? globalWeights;
    if (p.StartsWith("mc:", StringComparison.Ordinal)) return new SearchPolicy(engine, int.Parse(p[3..]), weights, threads: 1);
    // vn:N[:D[:R]]: the search with the value network at the leaves, D days on (default 3, the best of 1-6); R the
    // playouts' own moves by the network too (0 never, 1 the morning's Destination, 2 every decision).
    if (p.StartsWith("vn:", StringComparison.Ordinal))
    {
        var parts = p.Split(':');
        return new SearchPolicy(engine, int.Parse(parts[1]), weights, threads: 1, net: net ?? throw new InvalidOperationException("no value network (models/solver/value.bin, --net)"),
            leafDays: parts.Length > 2 ? int.Parse(parts[2]) : 3, rolloutNet: parts.Length > 3 ? int.Parse(parts[3]) : 0);
    }
    return p switch
    {
        "greedy" => new GreedyPolicy(seed: seed),
        "heuristic" => new HeuristicPolicy(engine, weights, seed),
        _ => new RandomPolicy(seed),
    };
}

EvalWeights LoadWeights()
{
    string? path = Opt("--weights");
    if (path == null)
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "models", "solver", "weights.txt");
            if (File.Exists(candidate))
            {
                path = candidate;
                break;
            }
        }
    if (path == null) return EvalWeights.Default;
    Console.Error.WriteLine($"weights: {path}");
    return WeightsFile.Read(File.ReadAllText(path));
}

int Instruments()
{
    var tuned = Tuning.Instruments(engine, weights, IntOpt("--runs", 6000));
    Tuning.Save(tuned, Opt("--out"));
    return 0;
}

int Levels()
{
    // levels [--runs N] [--out file]: each hint level's time (median, half the cores) and Chromatic per run (same seeds) -> models/solver/levels.txt.
    int runs = IntOpt("--runs", 400), threads = Math.Max(2, Environment.ProcessorCount / 2);
    var net = valueNet ?? throw new InvalidOperationException("no value network (models/solver/value.bin)");
    var states = SampleStates(120);
    var search = new MonteCarloSearch(engine, weights) { Threads = threads, Net = net };
    search.Search(states[0], 256, seed: 1);
    var levels = new List<HintLevel>();
    foreach (int n in new[] { 32, 64, 128, 256, 512, 1024, 2048 })
    {
        var ms = states.Select(st => search.Search(st, n, seed: 7).Elapsed.TotalMilliseconds).OrderBy(x => x).ToArray();
        var chroma = new double[runs];
        Parallel.For(0, runs, r =>
        {
            ulong seed = 9 * 1_000_003UL + (ulong)r;
            var s = engine.NewRun(seed);
            Runs.PlayOut(engine, ref s, new SearchPolicy(engine, n, weights, threads: 1, net: net));
            chroma[r] = RunScore.Of(s).Chromatic;
        });
        var level = new HintLevel(n, ms[ms.Length / 2], chroma.Average());
        levels.Add(level);
        Console.WriteLine($"{n,5} playouts per action: {level.Milliseconds:0.0} ms, Chromatic {level.Chromatic:0.00}");
    }
    string path = Opt("--out") ?? Path.Combine("models", "solver", "levels.txt");
    File.WriteAllText(path, HintLevels.Write(levels));
    Console.WriteLine($"saved {path}");
    return 0;
}

List<GameState> SampleStates(int count)
{
    var states = new List<GameState>();
    var policy = new HeuristicPolicy(engine, weights, 5);
    Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
    var pick = new Rng(9);
    for (ulong seed = 1; states.Count < count; seed++)
    {
        var s = engine.NewRun(seed);
        while (!s.Ended)
        {
            int k = engine.Legal(s, legal);
            if (k > 1 && pick.Chance(8)) states.Add(s);
            engine.Apply(ref s, policy.Choose(engine, s, legal[..k]));
        }
    }
    return states;
}

int Latency()
{
    // latency [--states N] [--threads T]: how long a hint takes for each budget (playouts per action), as the app searches.
    int count = IntOpt("--states", 120), threads = IntOpt("--threads", Math.Max(2, Environment.ProcessorCount / 2));
    var states = new List<GameState>();
    var policy = new HeuristicPolicy(engine, weights, 5);
    Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
    var pick = new Rng(9);
    for (ulong seed = 1; states.Count < count; seed++)
    {
        var s = engine.NewRun(seed);
        while (!s.Ended)
        {
            int k = engine.Legal(s, legal);
            if (k > 1 && pick.Chance(8)) states.Add(s);
            engine.Apply(ref s, policy.Choose(engine, s, legal[..k]));
        }
    }
    var search = new MonteCarloSearch(engine, weights) { Threads = threads, Net = valueNet };
    search.Search(states[0], 256, seed: 1); // warm up
    Console.WriteLine($"{states.Count} positions, {threads} threads, value network {(valueNet != null ? "on" : "off")}");
    foreach (int n in new[] { 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048 })
    {
        var ms = states.Select(s => search.Search(s, n, seed: 7).Elapsed.TotalMilliseconds).OrderBy(x => x).ToArray();
        Console.WriteLine($"{n,5} playouts per action: median {ms[ms.Length / 2],6:0.0} ms, 90% {ms[(int)(ms.Length * 0.9)],6:0.0} ms, max {ms[^1],6:0.0} ms");
    }
    return 0;
}

int Compare()
{
    // compare --a P --b P [--net-a f] [--net-b f] [--weights-a f] [--weights-b f] [--runs N] [--seed S]: both
    // policies play the same seeds; the paired difference.
    int runs = IntOpt("--runs", 400);
    ulong seed0 = (ulong)IntOpt("--seed", 9);
    string a = Opt("--a") ?? "mc:64", b = Opt("--b") ?? "vn:64";
    var netA = Opt("--net-a") is { } fa ? ValueNet.Load(fa) : valueNet;
    var netB = Opt("--net-b") is { } fb ? ValueNet.Load(fb) : valueNet;
    var wA = Opt("--weights-a") is { } wa ? WeightsFile.Read(File.ReadAllText(wa)) : weights;
    var wB = Opt("--weights-b") is { } wb ? WeightsFile.Read(File.ReadAllText(wb)) : weights;
    var ua = new double[runs];
    var ub = new double[runs];
    var ca = new double[runs];
    var cb = new double[runs];
    var sw = Stopwatch.StartNew();
    Parallel.For(0, runs, r =>
    {
        ulong seed = seed0 * 1_000_003 + (ulong)r;
        var s = engine.NewRun(seed);
        Runs.PlayOut(engine, ref s, MakePolicy(seed, a, netA, wA));
        (ua[r], ca[r]) = (RunScore.Of(s).Utility, RunScore.Of(s).Chromatic);
        s = engine.NewRun(seed);
        Runs.PlayOut(engine, ref s, MakePolicy(seed, b, netB, wB));
        (ub[r], cb[r]) = (RunScore.Of(s).Utility, RunScore.Of(s).Chromatic);
    });
    var (mean, se) = Tuning.Paired(ub, ua);
    Console.WriteLine($"{runs} paired runs in {sw.Elapsed.TotalSeconds:0} s");
    Console.WriteLine($"a {a}: Chromatic {ca.Average():0.000}, utility {ua.Average():0.000}");
    Console.WriteLine($"b {b}: Chromatic {cb.Average():0.000}, utility {ub.Average():0.000}");
    Console.WriteLine($"b - a: {mean:+0.000;-0.000} ± {se:0.000}");
    Console.WriteLine($"better: {(mean > 2 * se ? "b" : mean < -2 * se ? "a" : "tie")}");
    return 0;
}

int SelfPlayCommand()
{
    // selfplay [--runs N] [--policy mc:32] [--seed S] [--out file]: value network training data from runs the policy plays.
    string policy = Opt("--policy") ?? "mc:32";
    SelfPlay.Run(engine, seed => MakePolicy(seed, policy),
        IntOpt("--runs", 1000), (ulong)IntOpt("--seed", 1), Opt("--out") ?? Path.Combine("dataset", "selfplay", "values.bin"));
    return 0;
}

int Tune()
{
    Tuning.Tune(engine, weights, IntOpt("--runs", 6000), IntOpt("--passes", 2), Opt("--out"));
    return 0;
}

int Trace()
{
    ulong seed = (ulong)IntOpt("--seed", 1);
    var s = engine.NewRun(seed);
    var policy = MakePolicy(seed);
    Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
    Console.Write(StateText.Describe(s));
    while (!s.Ended)
    {
        int k = engine.Legal(s, legal);
        var a = policy.Choose(engine, s, legal[..k]);
        Console.WriteLine($"> {a}   (of {string.Join(", ", legal[..k].ToArray())})");
        engine.Apply(ref s, a);
        Console.Write(StateText.Describe(s));
    }
    Console.WriteLine($"Riot: {RunScore.Of(s)}");
    return 0;
}

int Play()
{
    int runs = IntOpt("--runs", 10000), threads = IntOpt("--threads", Environment.ProcessorCount);
    ulong seed0 = (ulong)IntOpt("--seed", 1);
    var scores = new RunScore[runs];
    var steps = new int[runs];
    var sw = Stopwatch.StartNew();
    Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
    {
        var policy = MakePolicy(seed0 * 7919 + (ulong)t);
        for (int r = t; r < runs; r += threads)
        {
            var s = engine.NewRun(seed0 * 1_000_003 + (ulong)r);
            steps[r] = Runs.PlayOut(engine, ref s, policy);
            scores[r] = RunScore.Of(s);
        }
    });
    double secs = sw.Elapsed.TotalSeconds;
    Console.WriteLine($"{runs} runs, policy {Opt("--policy") ?? "random"}, {secs:0.00} s ({runs / secs:0} runs/s, {threads} threads), {steps.Average():0.0} decisions per run");
    Console.WriteLine($"Chromatic {scores.Average(x => x.Chromatic):0.000} · Ultimate {scores.Average(x => x.Ultimate):0.000} · Legendary {scores.Average(x => x.Legendary):0.000} · Rare {scores.Average(x => x.Rare):0.000} · utility {scores.Average(x => x.Utility):0.0000}");
    var hist = scores.GroupBy(x => x.Chromatic).OrderBy(g => g.Key).Select(g => $"{g.Key}: {100.0 * g.Count() / runs:0.0}%");
    Console.WriteLine("Chromatic per run: " + string.Join(", ", hist));
    return 0;
}

int Stats()
{
    // The rules' odds, measured over random runs, against what players' boards show.
    int runs = IntOpt("--runs", 20000);
    var policy = new RandomPolicy(5);
    int startInmates = 0, startFewer = 0, startMarker = 0, themes = 0, ironLaw = 0;
    int mornings = 0, roomReach = 0, roomOffered = 0, threeCards = 0, markerMornings = 0, multiMarkerMornings = 0;
    int offers = 0, speciesCards = 0, cards = 0;
    var objectives = new int[5];
    Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
    for (int r = 0; r < runs; r++)
    {
        var s = engine.NewRun((ulong)r + 1);
        int open = Engine.BoardCount(s);
        startInmates += open;
        if (open < 4) startFewer++;
        for (int n = 1; n < GameData.Cells; n++) if (s.Cells[n].Marker != Incident.None) { startMarker++; break; }
        if (s.Theme != Theme.None) themes++;
        if (s.Theme == Theme.IronLaw) ironLaw++;
        objectives[(int)s.Objective]++;
        int lastDay = 0;
        while (!s.Ended)
        {
            if (s.PendingCount > 0 && s.Pending[0].Kind == PendingKind.Instrument)
            {
                offers++;
                var p = s.Pending[0];
                for (int i = 0; i < p.Count; i++)
                {
                    cards++;
                    if (GameData.Info(p.Offer(i)).Group == InstrumentGroup.Species) speciesCards++;
                }
            }
            if (s.Phase == Phase.Move && s.PendingCount == 0 && s.Day != lastDay)
            {
                lastDay = s.Day;
                mornings++;
                if (s.DestinationCount == 3) threeCards++;
                int markers = 0;
                for (int n = 1; n < GameData.Cells; n++) if (s.Cells[n].Marker != Incident.None) markers++;
                if (markers > 0) markerMornings++;
                if (markers > 1) multiMarkerMornings++;
                bool reach = Engine.Steps(s.Position, GameData.Room) is >= 1 and <= GameData.Reach;
                if (reach && s.DestinationCount == 3)
                {
                    roomReach++;
                    for (int i = 0; i < s.DestinationCount; i++) if (s.Destinations[i] == GameData.Room) roomOffered++;
                }
            }
            int k = engine.Legal(s, legal);
            engine.Apply(ref s, policy.Choose(engine, s, legal[..k]));
        }
    }
    Console.WriteLine($"{runs} random runs");
    Console.WriteLine($"Inmates out at the start: {(double)startInmates / runs:0.00} on average, fewer than 4 in {100.0 * startFewer / runs:0.0}% of runs (boards: 4 in 95%)");
    Console.WriteLine($"A Prison Incident marker at the start: {100.0 * startMarker / runs:0.0}% (the user's frames: every run)");
    Console.WriteLine($"A theme: {100.0 * themes / runs:0.0}% of runs (game table: 12%), Iron Law {100.0 * ironLaw / runs:0.0}%");
    Console.WriteLine($"Objectives: {string.Join(", ", objectives.Select((c, i) => $"{GameData.Name((ObjectiveKind)i)} {100.0 * c / runs:0.0}%"))}");
    Console.WriteLine($"Mornings with 3 Destinations: {100.0 * threeCards / Math.Max(1, mornings):0.0}%");
    Console.WriteLine($"Mornings with a Prison Incident marker on the Sandbox: {100.0 * markerMornings / Math.Max(1, mornings):0.0}% (the user's frames: 95.5%), 2 or more {100.0 * multiMarkerMornings / Math.Max(1, mornings):0.0}% (the user's frames: 13.6%)");
    Console.WriteLine($"Interrogation Room among 3 Destinations when in reach: {100.0 * roomOffered / Math.Max(1, roomReach):0.0}% (the user's frames: 51.6% of 64)");
    Console.WriteLine($"Species instruments among offered cards: {100.0 * speciesCards / Math.Max(1, cards):0.0}% of {cards} cards in {offers} offers (boards: 3.9%)");
    return 0;
}

int Bench()
{
    double seconds = IntOpt("--seconds", 5);
    var policy = new RandomPolicy(3);
    long runs = 0, decisions = 0;
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        var s = engine.NewRun((ulong)runs + 1);
        decisions += Runs.PlayOut(engine, ref s, policy);
        runs++;
    }
    double secs = sw.Elapsed.TotalSeconds;
    Console.WriteLine($"{runs} random runs in {secs:0.0} s: {runs / secs:0} runs/s, {decisions / secs / 1e6:0.00} M decisions/s, {decisions / (double)runs:0.0} decisions per run (one thread)");
    return 0;
}
