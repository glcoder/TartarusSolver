using Tartarus.Rules;

namespace Tartarus.Solver;

/// <summary>
/// One step ahead by the static evaluation: each action is played on a copy (the same dice for all of them, so
/// they're compared on the same luck) and the best position after it wins. Instruments by their measured worth; an
/// offer whose best card is worth too little is re-rolled. Fast enough to play the Monte Carlo search's playouts.
/// </summary>
/// <remarks>
/// With the playouts' policy network (<paramref name="net"/>) the positions are compared by its estimate instead;
/// instrument picks and their Re-roll stay by the measured worths.
/// The step ahead is played on what the player knows: who is behind each door and which incident each unseen marker
/// holds are drawn again for it, so a search's playout, whose world has them all drawn already, doesn't pick the door
/// with the best Inmate behind it.
/// </remarks>
public sealed class HeuristicPolicy(Engine engine, EvalWeights? weights = null, ulong seed = 1, ValueNet? net = null) : IPolicy
{
    private readonly EvalWeights _w = weights ?? EvalWeights.Default;
    private Rng _rng = new(seed);

    public GameAction Choose(Engine _, in GameState s, ReadOnlySpan<GameAction> legal)
    {
        if (legal.Length == 1) return legal[0];
        if (legal[0].Kind == ActionKind.PickInstrument) return PickInstrument(s, legal);
        var view = s;
        engine.Determinize(ref view, _rng.Next());
        var dice = new Rng(_rng.Next());
        int best = 0;
        double bestValue = double.NegativeInfinity;
        for (int i = 0; i < legal.Length; i++)
        {
            var copy = view;
            copy.Rng = dice;
            engine.ApplyLegal(ref copy, legal[i]);
            double v = net != null ? net.Value(copy) : Evaluator.Value(copy, _w);
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
