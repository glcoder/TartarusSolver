using Tartarus.Solver;
using Tartarus.Vision;
using R = Tartarus.Rules;

/// <summary>
/// tartarus-scan sim &lt;frame.png&gt; [--seconds S]: the frame through the scanner into the rules, and the move hint
/// for it: every legal action ranked by the Monte Carlo search, as the app shows it. A pick screen plays on from an
/// empty Sandbox (the app uses the last one it saw).
/// </summary>
internal static class Sim
{
    public static int Run(string[] args, Func<string[], string, string?> opt, Func<string[], ISandboxScanner> makeScanner)
    {
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: tartarus-scan sim <frame.png> [--seconds S]");
            return 1;
        }
        double seconds = double.TryParse(opt(args, "--seconds"), System.Globalization.CultureInfo.InvariantCulture, out double sec) ? sec : 2;
        using var scanner = makeScanner(args);
        var scan = scanner.Scan(ImageIO.Load(args[0]));
        R.Observation observation;
        if (scan.Ok) observation = GameObservation.From(EditableSandbox.FromScan(scan), scan.Text);
        // --sandbox other.png: the Sandbox under a pick, read from another frame (as the app takes the last one it saw).
        else if (scan.Pick is { } pick && opt(args, "--sandbox") is { } under)
        {
            var below = scanner.Scan(ImageIO.Load(under));
            if (!below.Ok)
            {
                Console.Error.WriteLine($"no Sandbox in {under}: {below.Reason}");
                return 1;
            }
            observation = GameObservation.FromPick(pick, EditableSandbox.FromScan(below), below.Text);
        }
        else if (scan.Pick is { } pick2) observation = GameObservation.FromPick(pick2, EditableSandbox.Empty());
        else
        {
            Console.Error.WriteLine($"nothing to hint: {scan.Reason}");
            return 1;
        }
        // --instruments chains,muzzle: the instruments held, when the frame's slots don't show them (the app keeps them in its run memory).
        if (opt(args, "--instruments") is { } held)
        {
            observation.Instruments.Clear();
            foreach (var id in held.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (R.GameData.InstrumentById(id.Trim()) is { } i) observation.Instruments.Add(i);
        }
        var engine = new R.Engine();
        var state = engine.FromObservation(observation, 1);
        Console.Write(R.StateText.Describe(state));
        string? path = WeightsFile.Find();
        var weights = path != null ? WeightsFile.Read(File.ReadAllText(path)) : EvalWeights.Default;
        var net = opt(args, "--no-net") == null && !args.Contains("--no-net") && ValueNet.Find() is { } netPath ? ValueNet.Load(netPath) : null;
        // The playouts' policy network as the app has it.
        var policy = net != null && ValueNet.FindPolicy() is { } policyPath ? ValueNet.Load(policyPath) : null;
        var search = new MonteCarloSearch(engine, weights) { Threads = Math.Max(2, Environment.ProcessorCount / 2), Net = net, Policy = policy };
        Console.WriteLine(net != null ? "value network: on" : "value network: off");
        // Warm up first (JIT tiers, thread pool), as the app's search always is.
        search.Search(state, 20000, TimeSpan.FromSeconds(1), seed: 999);
        var result = search.Search(state, 20000, TimeSpan.FromSeconds(seconds));
        Console.WriteLine($"{result.Playouts:N0} playouts in {result.Elapsed.TotalSeconds:0.0} s (weights {(path ?? "default")}):");
        foreach (var a in result.Actions)
            Console.WriteLine($"  {a.Action,-50} Chromatic ≈ {a.Chromatic:0.00}  utility {a.Mean:0.000} ± {a.StdError:0.000}  ({a.Playouts} playouts)");
        return 0;
    }
}

/// <summary>tartarus-scan runs [--log path]: the Tartarus runs the game's log tells of.</summary>
internal static class LogRuns
{
    public static int Run(string[] args, Func<string[], string, string?> opt)
    {
        string? path = GameLog.FindPath(opt(args, "--log"));
        if (path == null)
        {
            Console.Error.WriteLine("UE_game.log not found; pass --log <path>");
            return 1;
        }
        var log = GameLog.Read(path);
        Console.WriteLine($"{path}: {log.Runs.Count} runs");
        foreach (var r in log.Runs)
            Console.WriteLine($"  {r.Start:yyyy-MM-dd HH:mm}  day {r.Day}/{r.MaxDays}  Chromatic {r.Chromatic,2}  Warden's Treasures {r.Treasures,2}  Prison Incidents {r.Incidents}  Consumes {r.Consumes,2}  instrument picks {r.InstrumentPicks}  Objective {(r.ObjectiveDone ? "done" : "-")}{(r.Finished ? "" : "  (unfinished)")}{(r.Paged ? "  (told by pages)" : "")}");
        var done = log.Runs.Where(r => r.Day >= r.MaxDays && r.MaxDays > 0 || r.Paged && r.Finished).ToList();
        if (done.Count > 0) Console.WriteLine($"finished runs: {done.Count}, Chromatic per run {done.Average(r => r.Chromatic):0.00}");
        return 0;
    }
}
