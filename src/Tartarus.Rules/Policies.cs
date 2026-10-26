namespace Tartarus.Rules;

/// <summary>Picks an action for a state. Implementations may keep scratch state: use one per thread.</summary>
public interface IPolicy
{
    GameAction Choose(Engine engine, in GameState s, ReadOnlySpan<GameAction> legal);
}

/// <summary>Uniformly random legal actions: for testing the rules and as the floor any strategy must beat.</summary>
public sealed class RandomPolicy(ulong seed = 1) : IPolicy
{
    private Rng _rng = new(seed);

    public GameAction Choose(Engine engine, in GameState s, ReadOnlySpan<GameAction> legal) => legal[_rng.Below(legal.Length)];
}

/// <summary>
/// One step ahead: each action is tried on a few copies (chance drawn anew each time) and scored by the Sandbox
/// right after: the objective's utility, plus a little for Inmates on the Sandbox and their rarities. Never stops a
/// chain early. Not a strategy, a sanity check: it must beat <see cref="RandomPolicy"/> clearly.
/// </summary>
public sealed class GreedyPolicy(int samples = 4, ulong seed = 1) : IPolicy
{
    private Rng _rng = new(seed);

    public GameAction Choose(Engine engine, in GameState s, ReadOnlySpan<GameAction> legal)
    {
        if (legal.Length == 1) return legal[0];
        int best = 0;
        double bestValue = double.NegativeInfinity;
        for (int i = 0; i < legal.Length; i++)
        {
            if (legal[i].Kind == ActionKind.StopChain) continue;
            double sum = 0;
            for (int k = 0; k < samples; k++)
            {
                var copy = s;
                copy.Rng = new Rng(_rng.Next());
                engine.Apply(ref copy, legal[i]);
                sum += Value(copy);
            }
            if (sum > bestValue)
            {
                bestValue = sum;
                best = i;
            }
        }
        return legal[best];
    }

    private static double Value(in GameState s)
    {
        double v = RunScore.Of(s).Utility * 100;
        for (int n = 1; n < GameData.Cells; n++)
            if (s.Cells[n].HasInmate) v += Math.Pow(3, (int)s.Cells[n].Inmate.Rarity) * 0.01;
        return v;
    }
}

public static class Runs
{
    /// <summary>Plays a run from <paramref name="s"/> to the Riot with <paramref name="policy"/>. Returns the decisions made.</summary>
    public static int PlayOut(Engine engine, ref GameState s, IPolicy policy, int maxSteps = 5000)
    {
        Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
        int steps = 0;
        while (!s.Ended && steps < maxSteps)
        {
            int k = engine.Legal(s, legal);
            if (k == 0) break;
            var a = policy.Choose(engine, s, legal[..k]);
            if (!engine.Apply(ref s, a)) throw new InvalidOperationException($"The policy chose an illegal action: {a}");
            steps++;
        }
        return steps;
    }
}
