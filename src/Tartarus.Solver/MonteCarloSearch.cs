using System.Diagnostics;
using Tartarus.Rules;

namespace Tartarus.Solver;

/// <summary>An action and what playing it led to over the playouts.</summary>
/// <summary>An action's mean worth over its playouts, and what the run ends with on average: Chromatics contained, Ultimate and Legendary Inmates left.</summary>
public sealed record ActionValue(GameAction Action, double Mean, double StdError, int Playouts, double Chromatic, double Ultimate = 0, double Legendary = 0);

public sealed record SearchResult(IReadOnlyList<ActionValue> Actions, int Playouts, TimeSpan Elapsed)
{
    public ActionValue? Best => Actions.Count > 0 ? Actions[0] : null;
}

/// <summary>
/// Ranks the legal actions of a position by playing each to the Riot many times: per playout the hidden is drawn
/// anew (who is behind each door, which incident each marker holds, the dice), the same for every action (common
/// random numbers), and the rest of the run is played by the heuristic policy. Rounds of playouts go on until the
/// budget is spent; actions far behind the leader stop getting playouts (successive rejection).
/// </summary>
public sealed class MonteCarloSearch(Engine engine, EvalWeights? weights = null)
{
    private readonly EvalWeights _w = weights ?? EvalWeights.Default;

    public int Threads { get; init; } = Math.Max(1, Environment.ProcessorCount - 2);

    /// <summary>
    /// The value network: when set, a playout runs only to a later morning (<see cref="LeafDays"/> days on) and the
    /// network says how the run ends from there; without it playouts run to the Riot.
    /// </summary>
    public ValueNet? Net { get; init; }
    public int LeafDays { get; init; } = 3;
    /// <summary>The playouts' own moves by the value network too: 0 never, 1 the morning's Destination, 2 every decision.</summary>
    public int RolloutNet { get; init; }
    /// <summary>
    /// The playouts' policy network: when set, every decision of a playout (but the instrument picks) goes to the
    /// action whose after-state it scores highest, instead of the static evaluation's. Trained on the search's own
    /// choices (tools/train/policynet.py).
    /// </summary>
    public ValueNet? Policy { get; init; }
    /// <summary>The policy network takes the playouts' instrument picks (and Re-rolls) too: one trained on them.</summary>
    public bool PolicyInstruments { get; init; }
    /// <summary>A network of their own for the playouts' instrument picks (and Re-rolls), whatever takes the rest.</summary>
    public ValueNet? InstrumentPolicy { get; init; }
    /// <summary>The playouts' moves look at the doors their world has drawn (before 0.7.10; for comparison): <see cref="HeuristicPolicy"/>.</summary>
    public bool PeekingPlayouts { get; init; }

    /// <summary>Plays on from <paramref name="s"/> and returns the run's (predicted) utility, Chromatic, Ultimate and Legendary counts.</summary>
    private (double Utility, double Chromatic, double Ultimate, double Legendary) Playout(ref GameState s, IPolicy policy)
    {
        if (Net == null)
        {
            Runs.PlayOut(engine, ref s, policy);
            var score = RunScore.Of(s);
            return (score.Utility, score.Chromatic, score.Ultimate, score.Legendary);
        }
        int leafDay = s.Day + LeafDays;
        Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
        for (int steps = 0; !s.Ended && steps < 2000; steps++)
        {
            if (s.Phase == Phase.Move && s.PendingCount == 0 && s.Day >= leafDay)
            {
                Span<float> x = stackalloc float[Features.Count];
                Features.Encode(s, x);
                Span<float> y = stackalloc float[Net.Outputs];
                Net.Forward(x, y);
                ValueNet.Bound(y, s);
                return Net.Outputs >= 4 ? (ValueNet.Utility(y), y[1], y[2], y[3]) : (y[0], s.Chromatic, 0, 0);
            }
            int k = engine.Legal(s, legal);
            engine.Apply(ref s, policy.Choose(engine, s, legal[..k]));
        }
        var final = RunScore.Of(s);
        return (final.Utility, final.Chromatic, final.Ultimate, final.Legendary);
    }

    public SearchResult Search(in GameState root, int playoutsPerAction, TimeSpan? timeLimit = null, CancellationToken cancel = default, ulong seed = 1, Action<SearchResult>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        Span<GameAction> buf = stackalloc GameAction[Engine.MaxActions];
        int k = engine.Legal(root, buf);
        var actions = buf[..k].ToArray();
        // One action is played out too: the hint shows what the run is expected to end with.
        if (k == 0) return new SearchResult([], 0, sw.Elapsed);

        var sum = new double[k];
        var sumSq = new double[k];
        var chroma = new double[k];
        var ultimate = new double[k];
        var legendary = new double[k];
        var count = new int[k];
        var alive = Enumerable.Repeat(true, k).ToArray();
        var gate = new object();
        var start = root;
        int round = 0, total = 0;
        int RoundSize = Math.Clamp(playoutsPerAction, 1, 64);
        while (!cancel.IsCancellationRequested)
        {
            int baseSample = round * RoundSize;
            var live = Enumerable.Range(0, k).Where(i => alive[i]).ToArray();
            Parallel.For(0, RoundSize, new ParallelOptions { MaxDegreeOfParallelism = Threads, CancellationToken = CancellationToken.None }, j =>
            {
                ulong sampleSeed = Rng.Mix(seed * 0x9E3779B97F4A7C15UL + (ulong)(baseSample + j));
                var det = start;
                engine.Determinize(ref det, sampleSeed, fillHidden: true);
                var policy = new HeuristicPolicy(engine, _w, sampleSeed ^ 0x5DEECE66DUL, Policy ?? (RolloutNet > 0 ? Net : null), movesOnly: Policy == null && RolloutNet == 1,
                    instrumentsByNet: Policy != null && PolicyInstruments, instrumentNet: InstrumentPolicy, peek: PeekingPlayouts);
                foreach (int i in live)
                {
                    var s = det;
                    s.Rng = new Rng(sampleSeed + 1);
                    engine.Apply(ref s, actions[i]);
                    var (u, c, ult, leg) = Playout(ref s, policy);
                    lock (gate)
                    {
                        sum[i] += u;
                        sumSq[i] += u * u;
                        chroma[i] += c;
                        ultimate[i] += ult;
                        legendary[i] += leg;
                        count[i]++;
                    }
                }
            });
            total += RoundSize * live.Length;
            round++;
            progress?.Invoke(new SearchResult(Ranked(), total, sw.Elapsed));
            if (count.Where((c, i) => alive[i]).Min() >= playoutsPerAction) break;
            if (timeLimit is { } limit && sw.Elapsed >= limit) break;
            // Successive rejection: an action whose mean is more than 4 standard errors below the leader's stops.
            if (round >= 2)
            {
                int lead = Enumerable.Range(0, k).Where(i => alive[i]).MaxBy(i => sum[i] / count[i]);
                double leadMean = sum[lead] / count[lead];
                for (int i = 0; i < k; i++)
                    if (alive[i] && i != lead && sum[i] / count[i] + 4 * StdErr(i) < leadMean - 4 * StdErr(lead)) alive[i] = false;
            }
        }
        return new SearchResult(Ranked(), total, sw.Elapsed);

        List<ActionValue> Ranked() => Enumerable.Range(0, k)
            .Where(i => count[i] > 0)
            .Select(i => new ActionValue(actions[i], sum[i] / count[i], StdErr(i), count[i], chroma[i] / count[i], ultimate[i] / count[i], legendary[i] / count[i]))
            .OrderByDescending(a => a.Mean)
            .ToList();

        double StdErr(int i)
        {
            if (count[i] < 2) return double.PositiveInfinity;
            double mean = sum[i] / count[i];
            double variance = Math.Max(0, sumSq[i] / count[i] - mean * mean);
            return Math.Sqrt(variance / count[i]);
        }
    }
}

/// <summary>A policy that searches at every decision: for measuring how well the search plays.</summary>
public sealed class SearchPolicy(Engine engine, int playouts, EvalWeights? weights = null, int threads = 1, ValueNet? net = null, int leafDays = 3, int rolloutNet = 0, ValueNet? policy = null, bool policyInstruments = false, ValueNet? instrumentPolicy = null, bool peek = false) : IPolicy
{
    private readonly MonteCarloSearch _search = new(engine, weights) { Threads = threads, Net = net, LeafDays = leafDays, RolloutNet = rolloutNet, Policy = policy, PolicyInstruments = policyInstruments, InstrumentPolicy = instrumentPolicy, PeekingPlayouts = peek };
    private ulong _seed = 1;

    /// <summary>The last search's value of its position (the best action's mean utility); null when there was nothing to choose.</summary>
    public double? LastValue => LastResult?.Best!.Mean;
    /// <summary>The last search, every action's mean; null when there was nothing to choose.</summary>
    public SearchResult? LastResult { get; private set; }

    public GameAction Choose(Engine _, in GameState s, ReadOnlySpan<GameAction> legal)
    {
        LastResult = null;
        if (legal.Length == 1) return legal[0];
        LastResult = _search.Search(s, playouts, seed: _seed++);
        return LastResult.Best!.Action;
    }
}
