using Tartarus.Rules;

namespace Tartarus.Solver;

/// <summary>
/// One step ahead by the static evaluation: each action is played on a copy (the same dice for all of them, so
/// they're compared on the same luck) and the best position after it wins. Instruments by their measured worth; an
/// offer whose best card is worth too little is re-rolled. Fast enough to play the Monte Carlo search's playouts.
/// </summary>
/// <remarks>
/// With a value network (<paramref name="net"/>) the positions are compared by its estimate instead: all decisions,
/// or only the morning's Destination (<paramref name="movesOnly"/>). Slower, closer to how the search plays. The
/// instrument picks and their Re-roll by it too only when it was trained on them (<paramref name="instrumentsByNet"/>),
/// or by a network of their own (<paramref name="instrumentNet"/>): the after-state of a Re-roll is the run without a
/// new instrument and one Re-roll fewer.
/// The step ahead is played on what the player knows: who is behind each door and which incident each unseen marker
/// holds are drawn again for it, so a search's playout, whose world has them all drawn already, doesn't pick the door
/// with the best Inmate behind it. <paramref name="peek"/> looks at the world's own (before 0.7.10; for comparison).
/// </remarks>
public sealed class HeuristicPolicy(Engine engine, EvalWeights? weights = null, ulong seed = 1, ValueNet? net = null, bool movesOnly = false, bool instrumentsByNet = false, ValueNet? instrumentNet = null, bool peek = false) : IPolicy
{
    private readonly EvalWeights _w = weights ?? EvalWeights.Default;
    private Rng _rng = new(seed);

    public GameAction Choose(Engine _, in GameState s, ReadOnlySpan<GameAction> legal)
    {
        if (legal.Length == 1) return legal[0];
        bool pick = legal[0].Kind == ActionKind.PickInstrument;
        var scorer = pick ? instrumentNet ?? (instrumentsByNet ? net : null) : net;
        if (pick && scorer == null) return PickInstrument(s, legal);
        var view = s;
        if (!peek) engine.Determinize(ref view, _rng.Next());
        var dice = new Rng(_rng.Next());
        int best = 0;
        double bestValue = double.NegativeInfinity;
        bool byNet = scorer != null && (pick || !movesOnly || legal[0].Kind == ActionKind.Move);
        for (int i = 0; i < legal.Length; i++)
        {
            var copy = view;
            copy.Rng = dice;
            engine.Apply(ref copy, legal[i]);
            double v = byNet ? scorer!.Value(copy) : Evaluator.Value(copy, _w);
            if (v > bestValue)
            {
                bestValue = v;
                best = i;
            }
        }
        return legal[best];
    }

    private GameAction PickInstrument(in GameState s, ReadOnlySpan<GameAction> legal)
    {
        int best = -1;
        double bestValue = double.NegativeInfinity;
        bool canReroll = false;
        for (int i = 0; i < legal.Length; i++)
        {
            if (legal[i].Kind == ActionKind.Reroll)
            {
                canReroll = true;
                continue;
            }
            double v = _w.InstrumentValue[legal[i].Arg];
            if (v > bestValue)
            {
                bestValue = v;
                best = i;
            }
        }
        if (canReroll && bestValue < _w.RerollBelow) return new GameAction(ActionKind.Reroll);
        return legal[best];
    }
}
