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
    /// <summary>
    /// A probe of deeper search: the decision after each action is searched too, open-loop (a follow-up is the same
    /// action in every world where it's legal; where it isn't, the playout policy's pick there stands in), and an
    /// action is worth its best follow-up's mean. The leaf stays where the action alone would put it.
    /// </summary>
    public bool TwoPly { get; init; }

    /// <summary>Plays on from <paramref name="s"/> and returns the run's (predicted) utility, Chromatic, Ultimate and Legendary counts.</summary>
    private (double Utility, double Chromatic, double Ultimate, double Legendary) Playout(ref GameState s, IPolicy policy, int? leafAt = null)
    {
        if (Net == null)
        {
            Runs.PlayOut(engine, ref s, policy);
            var score = RunScore.Of(s);
            return (score.Utility, score.Chromatic, score.Ultimate, score.Legendary);
        }
        int leafDay = leafAt ?? s.Day + LeafDays;
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

        var own = Enumerable.Range(0, k).Select(_ => new Tally()).ToArray();
        // TwoPly: each action's follow-ups, and the worlds it was played in.
        var follow = Enumerable.Range(0, k).Select(_ => new Dictionary<GameAction, Tally>()).ToArray();
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
            int played = 0;
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
                    if (TwoPly)
                    {
                        int n = FollowUps(i, s, policy);
                        Interlocked.Add(ref played, n);
                        continue;
                    }
                    var v = Playout(ref s, policy);
                    Interlocked.Increment(ref played);
                    lock (gate)
                    {
                        own[i].Add(v);
                        count[i]++;
                    }
                }
            });
            total += played;
            round++;
            progress?.Invoke(new SearchResult(Ranked(), total, sw.Elapsed));
            if (count.Where((c, i) => alive[i]).Min() >= playoutsPerAction) break;
            if (timeLimit is { } limit && sw.Elapsed >= limit) break;
            // Successive rejection: an action whose mean is more than 4 standard errors below the leader's stops.
            if (round >= 2)
            {
                int lead = Enumerable.Range(0, k).Where(i => alive[i]).MaxBy(i => Worth(i).Mean);
                var leader = Worth(lead);
                for (int i = 0; i < k; i++)
                    if (alive[i] && i != lead && Worth(i).Mean + 4 * Worth(i).StdErr < leader.Mean - 4 * leader.StdErr) alive[i] = false;
            }
        }
        return new SearchResult(Ranked(), total, sw.Elapsed);

        List<ActionValue> Ranked() => Enumerable.Range(0, k)
            .Where(i => count[i] > 0)
            .Select(i =>
            {
                var t = Worth(i);
                return new ActionValue(actions[i], t.Mean, t.StdErr, count[i], t.Chromatic / t.Count, t.Ultimate / t.Count, t.Legendary / t.Count);
            })
            .OrderByDescending(a => a.Mean)
            .ToList();

        // What action i is worth: its own playouts, or (TwoPly) its best follow-up among those played in at least
        // half its worlds (one found late hasn't met the same worlds).
        Tally Worth(int i)
        {
            if (!TwoPly) return own[i];
            lock (gate)
                return follow[i].Values.Where(t => 2 * t.Count >= count[i]).MaxBy(t => t.Mean) ?? new Tally();
        }

        // TwoPly: every follow-up legal in this world played out from the same dice; a known follow-up not legal here
        // gets what the playout policy's pick here brought. The playouts made.
        int FollowUps(int i, GameState s, IPolicy policy)
        {
            int leafDay = s.Day + LeafDays;
            Span<GameAction> next = stackalloc GameAction[Engine.MaxActions];
            int n = s.Ended ? 0 : engine.Legal(s, next);
            var results = new (GameAction Action, (double, double, double, double) Value)[Math.Max(n, 1)];
            int pick = 0;
            if (n == 0) results[0] = (Over, Playout(ref s, policy, leafDay));
            else
            {
                var chosen = policy.Choose(engine, s, next[..n]);
                for (int f = 0; f < n; f++)
                {
                    var t = s;
                    engine.Apply(ref t, next[f]);
                    results[f] = (next[f], Playout(ref t, policy, leafDay));
                    if (next[f] == chosen) pick = f;
                }
            }
            lock (gate)
            {
                var seen = follow[i];
                foreach (var (a, v) in results)
                {
                    if (!seen.TryGetValue(a, out var t)) seen[a] = t = new Tally();
                    t.Add(v);
                }
                foreach (var (a, t) in seen)
                    if (Array.FindIndex(results, r => r.Action == a) < 0) t.Add(results[pick].Value);
                count[i]++;
            }
            return results.Length;
        }
    }

    /// <summary>TwoPly's follow-up of an action that ends the run.</summary>
    private static readonly GameAction Over = new(ActionKind.End, 255);

    /// <summary>Playout results summed: utility (mean, standard error) and the counts the run ends with.</summary>
    private sealed class Tally
    {
        public double Sum, SumSq, Chromatic, Ultimate, Legendary;
        public int Count;
        public double Mean => Count > 0 ? Sum / Count : double.NegativeInfinity;

        public double StdErr
        {
            get
            {
                if (Count < 2) return double.PositiveInfinity;
                double variance = Math.Max(0, SumSq / Count - Mean * Mean);
                return Math.Sqrt(variance / Count);
            }
        }

        public void Add((double Utility, double Chromatic, double Ultimate, double Legendary) v)
        {
            Sum += v.Utility;
            SumSq += v.Utility * v.Utility;
            Chromatic += v.Chromatic;
            Ultimate += v.Ultimate;
            Legendary += v.Legendary;
            Count++;
        }
    }
}

/// <summary>A policy that searches at every decision: for measuring how well the search plays.</summary>
public sealed class SearchPolicy(Engine engine, int playouts, EvalWeights? weights = null, int threads = 1, ValueNet? net = null, int leafDays = 3, int rolloutNet = 0, ValueNet? policy = null, bool policyInstruments = false, ValueNet? instrumentPolicy = null, bool peek = false, bool twoPly = false) : IPolicy
{
    private readonly MonteCarloSearch _search = new(engine, weights) { Threads = threads, Net = net, LeafDays = leafDays, RolloutNet = rolloutNet, Policy = policy, PolicyInstruments = policyInstruments, InstrumentPolicy = instrumentPolicy, PeekingPlayouts = peek, TwoPly = twoPly };
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
