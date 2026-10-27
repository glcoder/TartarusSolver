using System.IO;
using Tartarus.Rules;
using Tartarus.Solver;
using Tartarus.Vision;

namespace Tartarus.App;

/// <summary>A move hint: the position searched and its actions ranked, as far as the search has got.</summary>
public sealed record Hint(GameState State, SearchResult Result, bool ConsumeOnly, bool Final)
{
    /// <summary>The actions to show: on the Consume target pick only the targets, and stopping as "Cancel".</summary>
    public IReadOnlyList<ActionValue> Shown => ConsumeOnly
        ? Result.Actions.Where(a => a.Action.Kind is ActionKind.Consume or ActionKind.Chain).ToList()
        : Result.Actions;

    /// <summary>Within this of the best's worth (in Chromatics) a move is as good: the playouts can't tell them apart.</summary>
    public const double TieWithin = 0.05;

    /// <summary>The other moves as good as the best one.</summary>
    public IReadOnlyList<ActionValue> Ties => Shown.Skip(1).Where(a => Shown[0].Mean - a.Mean < TieWithin).ToList();
}

/// <summary>
/// Searches the scanned position in the background (Monte Carlo over the rules) and reports the ranking once the
/// time is up: only a firm hint is shown, so no early guess gets clicked. A new position cancels the old search; the
/// same position again keeps it. Uses half the cores, so the game keeps its frame rate.
/// </summary>
public sealed class HintService : IDisposable
{
    private readonly Engine _engine = new();
    private readonly MonteCarloSearch _search;
    private CancellationTokenSource? _cts;
    private string? _key;
    private string? _lastText;
    private Hint? _shown;

    /// <summary>The last search started: done once its hint is out (or it was cancelled).</summary>
    public Task Searching { get; private set; } = Task.CompletedTask;

    /// <summary>A shown move stays while still legal and no more than this behind the new best (worth, in Chromatics): flicker between near-equal moves helps nobody.</summary>
    public double KeepWithin { get; set; } = 0.15;

    /// <summary>A line for the log: what changed in the position since the last search (diagnostics).</summary>
    public event Action<string>? Log;

    public HintService(int playouts = 1024, double seconds = 0.5)
    {
        Playouts = playouts;
        Seconds = seconds;
        WeightsPath = WeightsFile.Find();
        var weights = WeightsPath != null ? WeightsFile.Read(File.ReadAllText(WeightsPath)) : EvalWeights.Default;
        // The value network when trained (models/solver/value.bin): playouts to the next morning, then its estimate.
        Net = ValueNet.Find() is { } net ? ValueNet.Load(net) : null;
        // The playouts' policy network when trained (models/solver/policy.bin): their moves as the search would make them.
        Policy = ValueNet.FindPolicy() is { } policy ? ValueNet.Load(policy) : null;
        // The playouts' instrument picks by a network of their own when there is one (models/solver/picks.bin).
        Picks = ValueNet.FindPicks() is { } picks ? ValueNet.Load(picks) : null;
        _search = new MonteCarloSearch(_engine, weights) { Threads = Math.Max(2, Environment.ProcessorCount / 2), Net = Net, Policy = Policy, InstrumentPolicy = Picks };
        // Warm up (the JIT's optimized code, the thread pool) so the first real hint is as quick as the rest.
        _ = Task.Run(() =>
        {
            var s = _engine.NewRun(12345);
            _search.Search(s, 20000, TimeSpan.FromSeconds(1.5));
        });
    }

    /// <summary>Playouts per action: the hint's quality.</summary>
    public int Playouts { get; set; }
    /// <summary>The most a hint may take.</summary>
    public double Seconds { get; set; }
    public string? WeightsPath { get; }
    public ValueNet? Net { get; }
    public ValueNet? Policy { get; }
    public ValueNet? Picks { get; }

    /// <summary>Raised on a thread-pool thread with the final ranking of a position.</summary>
    public event Action<Hint>? Ready;

    /// <summary>Starts searching the position the screen shows, unless it's the one being searched. True when a new search started (the old hint no longer fits).</summary>
    public bool Request(Observation observation, bool consumeOnly)
    {
        var state = _engine.FromObservation(observation, 1);
        if (state.Ended) return false;
        string key = StateText.Describe(state) + consumeOnly;
        if (key + Playouts == _key) return false;
        _key = key + Playouts;
        string text = StateText.Describe(state) + (consumeOnly ? "(Consume target pick)" : "");
        if (_lastText != null)
        {
            var before = _lastText.Split('|', '\n').Select(x => x.Trim()).ToHashSet();
            var changed = text.Split('|', '\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !before.Contains(x)).ToList();
            if (changed.Count > 0) Log?.Invoke("new position: " + string.Join(" | ", changed.Take(8)));
        }
        _lastText = text;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var limit = TimeSpan.FromSeconds(Seconds);
        Searching = Task.Run(() =>
        {
            try
            {
                var result = _search.Search(state, playoutsPerAction: Playouts, timeLimit: limit, cancel: cts.Token);
                if (cts.IsCancellationRequested) return;
                // Nothing to choose (only End) still gets its frame: the game ends such a day by itself, but not when a
                // merge took the Warden's Inmate away, and then it waits for the click.
                if (result.Actions.Count == 0) return;
                result = Steady(state, result, consumeOnly);
                var hint = new Hint(state, result, consumeOnly, Final: true);
                _shown = hint;
                Ready?.Invoke(hint);
            }
            catch (OperationCanceledException)
            {
            }
        });
        return true;
    }

    /// <summary>
    /// The shown move stays first when it is still among the actions and the new best beats it by no more than
    /// <see cref="KeepWithin"/>: a slightly different reading of the same screen doesn't flip the hint. Not
    /// across another day, phase or Warden's Inmate: that is another decision, not another reading of it.
    /// </summary>
    private SearchResult Steady(in GameState state, SearchResult result, bool consumeOnly)
    {
        if (_shown?.Shown is not { Count: > 0 } old || result.Actions.Count < 2 || _shown.ConsumeOnly != consumeOnly) return result;
        var before = _shown.State;
        if (before.Day != state.Day || before.Phase != state.Phase || before.Position != state.Position
            || before.Cells[before.Position].Inmate.Code != state.Cells[state.Position].Inmate.Code
            || before.Cells[before.Position].Inmate.Rarity != state.Cells[state.Position].Inmate.Rarity) return result;
        var keep = old[0].Action;
        var best = result.Actions[0];
        var same = result.Actions.FirstOrDefault(a => a.Action == keep);
        if (same == null || same == best || best.Mean - same.Mean > KeepWithin) return result;
        var reordered = new List<ActionValue> { same };
        reordered.AddRange(result.Actions.Where(a => a != same));
        return result with { Actions = reordered };
    }

    /// <summary>Forget the position (a non-game screen): the next one is searched afresh.</summary>
    public void Clear()
    {
        _cts?.Cancel();
        _key = null;
        _shown = null;
    }

    public void Dispose() => _cts?.Cancel();
}
