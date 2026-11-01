using System.IO;
using Tartarus.Rules;
using Tartarus.Solver;
using Tartarus.Vision;

namespace Tartarus.App;

/// <summary>A move hint: the position searched and its actions ranked, as far as the search has got.</summary>
public sealed record Hint(GameState State, SearchResult Result, bool ConsumeOnly, bool Final, bool HuntOnly = false)
{
    /// <summary>The actions to show: on the Consume target pick only the targets, and stopping as "Cancel"; on the Hunt's, its targets.</summary>
    public IReadOnlyList<ActionValue> Shown => ConsumeOnly
        ? Result.Actions.Where(a => a.Action.Kind is ActionKind.Consume or ActionKind.Chain).ToList()
        : HuntOnly ? Result.Actions.Where(a => a.Action.Kind == ActionKind.Hunt).ToList()
        : Result.Actions;

    /// <summary>Within this of the best's worth (in Chromatics) a move is as good: the playouts can't tell them apart.</summary>
    public const double TieWithin = 0.05;

    /// <summary>The other moves as good as the best one.</summary>
    public IReadOnlyList<ActionValue> Ties => Shown.Skip(1).Where(a => Shown[0].Mean - a.Mean < TieWithin).ToList();

    /// <summary>
    /// The moves as the overlay ranks them: on the Consume target pick the best one but consuming (made by Cancel first)
    /// among the targets. In the order of <see cref="SearchResult.Actions"/>: by worth, but for a shown move kept first.
    /// </summary>
    public IReadOnlyList<ActionValue> Ranked => ConsumeOnly
        && Result.Actions.FirstOrDefault(a => a.Action.Kind is not (ActionKind.Consume or ActionKind.Chain)) is { } stop
        ? Shown.Append(stop).OrderBy(Rank).ToList()
        : Shown;

    /// <summary>Where a move stands in the hint's order (0 = the one framed bold).</summary>
    public int Rank(ActionValue a)
    {
        for (int i = 0; i < Result.Actions.Count; i++)
            if (Result.Actions[i] == a) return i;
        return int.MaxValue;
    }
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
    // A search started ahead on a reading not settled yet: its hint waits here until the position is confirmed.
    private readonly object _gate = new();
    private bool _confirmed;
    private Hint? _held;

    /// <summary>The last search started: done once its hint is out (or it was cancelled).</summary>
    public Task Searching { get; private set; } = Task.CompletedTask;

    /// <summary>The search a scan waits for: not one started ahead, which runs alongside the scan that confirms it.</summary>
    public Task SearchingConfirmed
    {
        get { lock (_gate) return _confirmed ? Searching : Task.CompletedTask; }
    }

    /// <summary>
    /// A shown move stays while still legal and no more than this behind the new best (worth, in Chromatics): flicker
    /// between near-equal moves helps nobody. As good as the best (<see cref="Hint.TieWithin"/>), not more: 0.15 kept a
    /// worse move once a better reading came (a Chromatic count from the log, a cell read right).
    /// </summary>
    public double KeepWithin { get; set; } = Hint.TieWithin;

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
        // The chests shown as runs end with them, not as the playouts got them (models/solver/chests.txt).
        Chests = ChestCalibration.Load();
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
    public ChestCalibration? Chests { get; }

    /// <summary>Raised on a thread-pool thread with the final ranking of a position.</summary>
    public event Action<Hint>? Ready;

    /// <summary>
    /// Starts searching the position the screen shows, unless it's the one being searched. True when a new search
    /// started (the old hint no longer fits). <paramref name="ahead"/>: a reading not settled yet, searched ahead so the
    /// hint is ready when the next reading confirms it (2026-11-01: the wait for that reading and the search after it
    /// were 0.15-0.47 s of every hint, 0.3-0.5 s more on a slow PC); its hint is held until the same position is asked
    /// for without <paramref name="ahead"/>.
    /// </summary>
    public bool Request(Observation observation, bool consumeOnly, bool huntOnly = false, bool ahead = false)
    {
        var state = _engine.FromObservation(observation, 1);
        if (state.Ended) return false;
        string key = StateText.Describe(state) + consumeOnly + huntOnly;
        if (key + Playouts == _key)
        {
            if (ahead) return false;
            Hint? ready;
            lock (_gate)
            {
                if (_confirmed) return false;
                (_confirmed, ready, _held) = (true, _held, null);
                if (ready != null) _shown = ready;
            }
            // Searched ahead and done: out at once.
            if (ready != null) Ready?.Invoke(ready);
            return false;
        }
        // A confirmed search still running isn't given up for a reading that may be a passing misread.
        lock (_gate)
            if (ahead && _confirmed && !Searching.IsCompleted) return false;
        _key = key + Playouts;
        lock (_gate) (_confirmed, _held) = (!ahead, null);
        string text = StateText.Describe(state) + (consumeOnly ? "(Consume target pick)" : huntOnly ? "(Hunt target pick)" : "");
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
        int days = state.RemainingDays;
        Searching = Task.Run(() =>
        {
            try
            {
                var result = _search.Search(state, playoutsPerAction: Playouts, timeLimit: limit, cancel: cts.Token);
                if (cts.IsCancellationRequested) return;
                // Nothing to choose (only End) still gets its frame: the game ends such a day by itself, but not when a
                // merge took the Warden's Inmate away, and then it waits for the click.
                if (result.Actions.Count == 0) return;
                result = Steady(state, result, consumeOnly, huntOnly);
                if (Chests is { } chests) result = result with { Actions = [.. result.Actions.Select(a => Calibrated(a, chests, days))] };
                var hint = new Hint(state, result, consumeOnly, Final: true, huntOnly);
                lock (_gate)
                {
                    if (cts.IsCancellationRequested) return;
                    if (!_confirmed)
                    {
                        _held = hint;
                        return;
                    }
                    _shown = hint;
                }
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
    /// across another day, phase or Warden's Inmate: that is another decision, not another reading of it. The Consume
    /// (Hunt) card's target, framed dashed with the card, stays first on its target pick: the player saw it coming.
    /// </summary>
    private SearchResult Steady(in GameState state, SearchResult result, bool consumeOnly, bool huntOnly)
    {
        if (_shown?.Shown is not { Count: > 0 } old || result.Actions.Count < 2) return result;
        var shownKind = old[0].Action.Kind;
        bool toPick = !_shown.ConsumeOnly && !_shown.HuntOnly
            && (consumeOnly && shownKind is ActionKind.Consume or ActionKind.Chain || huntOnly && shownKind == ActionKind.Hunt);
        if (!toPick && (_shown.ConsumeOnly != consumeOnly || _shown.HuntOnly != huntOnly)) return result;
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

    /// <summary>An action's expected chests as runs end with them: the ranking (its mean) stays the search's.</summary>
    private static ActionValue Calibrated(ActionValue a, ChestCalibration chests, int days)
    {
        var (chromatic, ultimate) = chests.Adjust(days, a.Chromatic, a.Ultimate);
        return a with { Chromatic = chromatic, Ultimate = ultimate };
    }

    /// <summary>Forget the position (a non-game screen): the next one is searched afresh.</summary>
    public void Clear()
    {
        _cts?.Cancel();
        _key = null;
        _shown = null;
        lock (_gate) (_confirmed, _held) = (false, null);
    }

    public void Dispose() => _cts?.Cancel();
}
