using Tartarus.Rules;

namespace Tartarus.Solver;

/// <summary>
/// One step ahead by the static evaluation: each action is played on a copy (the same dice for all of them, so
/// they're compared on the same luck) and the best position after it wins. Instruments by their measured worth; an
/// offer whose best card is worth too little is re-rolled. Fast enough to play the Monte Carlo search's playouts.
/// </summary>
/// <remarks>
/// With a value network (<paramref name="net"/>) the positions are compared by its estimate instead: all decisions,
/// or only the morning's Destination (<paramref name="movesOnly"/>). Slower, closer to how the search plays.
/// </remarks>
public sealed class HeuristicPolicy(Engine engine, EvalWeights? weights = null, ulong seed = 1, ValueNet? net = null, bool movesOnly = false) : IPolicy
{
    private readonly EvalWeights _w = weights ?? EvalWeights.Default;
    private Rng _rng = new(seed);

    public GameAction Choose(Engine _, in GameState s, ReadOnlySpan<GameAction> legal)
    {
        if (legal.Length == 1) return legal[0];
        if (legal[0].Kind == ActionKind.PickInstrument) return PickInstrument(s, legal);
        var dice = new Rng(_rng.Next());
        int best = 0;
        double bestValue = double.NegativeInfinity;
        bool byNet = net != null && (!movesOnly || legal[0].Kind == ActionKind.Move);
        for (int i = 0; i < legal.Length; i++)
        {
            var copy = s;
            copy.Rng = dice;
            engine.Apply(ref copy, legal[i]);
            double v = byNet ? net!.Value(copy) : Evaluator.Value(copy, _w);
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
