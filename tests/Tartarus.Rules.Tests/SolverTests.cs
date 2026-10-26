using Tartarus.Rules;
using Tartarus.Solver;

namespace Tartarus.Rules.Tests;

public class SolverTests
{
    [Fact]
    public void The_heuristic_plays_whole_runs_legally_and_beats_random_play()
    {
        var engine = new Engine();
        double heuristic = 0, random = 0;
        for (ulong seed = 1; seed <= 300; seed++)
        {
            var s = engine.NewRun(seed);
            Runs.PlayOut(engine, ref s, new HeuristicPolicy(engine, seed: seed));
            Assert.True(s.Ended);
            heuristic += RunScore.Of(s).Utility;
            s = engine.NewRun(seed);
            Runs.PlayOut(engine, ref s, new RandomPolicy(seed));
            random += RunScore.Of(s).Utility;
        }
        Assert.True(heuristic > random * 2, $"heuristic {heuristic / 300:0.00} vs random {random / 300:0.00}");
    }

    [Fact]
    public void The_search_ranks_every_legal_action()
    {
        var engine = new Engine();
        var s = engine.NewRun(7);
        Runs.PlayOut(engine, ref s, new RandomPolicy(2), maxSteps: 6);
        var result = new MonteCarloSearch(engine) { Threads = 2 }.Search(s, 32);
        var legal = Setup.Legal(engine, s);
        Assert.Equal(legal.Count, result.Actions.Count);
        Assert.All(result.Actions, a => Assert.Contains(a.Action, legal));
        for (int i = 1; i < result.Actions.Count; i++) Assert.True(result.Actions[i - 1].Mean >= result.Actions[i].Mean);
    }

    [Fact]
    public void The_search_prefers_an_obvious_Chromatic()
    {
        // An Ultimate Wolf with an Ultimate Rat to eat on day 19 of 20: consuming makes a Chromatic now.
        var s = Setup.Empty(warden: 3);
        s.Day = 19;
        Setup.Put(ref s, 3, Code.Wolf, Rarity.Ultimate);
        Setup.Put(ref s, 9, Code.Rat, Rarity.Ultimate);
        var engine = new Engine();
        var result = new MonteCarloSearch(engine) { Threads = 2 }.Search(s, 64);
        Assert.Equal(new GameAction(ActionKind.Consume, 9), result.Best!.Action);
    }

    [Fact]
    public void A_sure_Ultimate_is_not_traded_for_a_long_shot_at_a_Chromatic()
    {
        // The run's last day (2026-10-26, a real position): the Warden on an Ultimate Wolf with no target. Replacing it
        // brings in a random Inmate that might eat its way up to a Chromatic now and then, but the Wolf's Ultimate cache
        // is lost nearly every time: End keeps it.
        var s = Setup.Empty(warden: 4);
        (s.Day, s.Chromatic, s.ObjectiveProgress, s.ObjectiveDone) = (20, 8, 18, true);
        Setup.Hold(ref s, Instrument.Saw, Instrument.Blacklist);
        Setup.Put(ref s, 4, Code.Wolf, Rarity.Ultimate);
        Setup.Put(ref s, 1, Code.Rat, Rarity.Magic);
        Setup.Put(ref s, 3, Code.Mammoth, Rarity.Magic);
        Setup.Put(ref s, 9, Code.Rat, Rarity.Magic);
        Setup.Put(ref s, 10, Code.Wolf, Rarity.Legendary);
        Setup.Put(ref s, 11, Code.Mammoth, Rarity.Magic);
        Setup.Put(ref s, 12, Code.Mammoth, Rarity.Rare);
        Setup.Put(ref s, 14, Code.Mammoth, Rarity.Legendary);
        Setup.Put(ref s, 15, Code.Rat, Rarity.Normal);
        var engine = new Engine();
        var result = new MonteCarloSearch(engine) { Threads = 2 }.Search(s, 2000);
        Assert.NotEqual(ActionKind.Replace, result.Best!.Action.Kind);
    }

    [Fact]
    public void Weights_survive_the_text_file()
    {
        var w = EvalWeights.Default with { Horizon = 7.5, RerollBelow = 1.1, InstrumentValue = Enumerable.Range(0, GameData.InstrumentCount).Select(i => i / 10.0).ToArray() };
        var back = WeightsFile.Read(WeightsFile.Write(w));
        Assert.Equal(w.Horizon, back.Horizon);
        Assert.Equal(w.RerollBelow, back.RerollBelow);
        Assert.Equal(w.Potential, back.Potential);
        Assert.Equal(w.InstrumentValue, back.InstrumentValue);
    }
}

public class ValueNetTests
{
    [Fact]
    public void The_network_computes_what_its_weights_say()
    {
        var rng = new Random(3);
        int n = Features.Count, h = 7, o = 4;
        float[] w1 = Enumerable.Range(0, h * n).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();
        float[] b1 = Enumerable.Range(0, h).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();
        float[] w2 = Enumerable.Range(0, o * h).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();
        float[] b2 = Enumerable.Range(0, o).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();
        string path = Path.GetTempFileName();
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(0x544E5654);
            w.Write(2);
            w.Write(h); w.Write(n); foreach (var v in w1) w.Write(v); foreach (var v in b1) w.Write(v);
            w.Write(o); w.Write(h); foreach (var v in w2) w.Write(v); foreach (var v in b2) w.Write(v);
        }
        var net = ValueNet.Load(path);
        var engine = new Engine();
        var s = engine.NewRun(5);
        var x = new float[n];
        Features.Encode(s, x);
        var hidden = new float[h];
        for (int j = 0; j < h; j++)
        {
            float v = b1[j];
            for (int i = 0; i < n; i++) v += w1[j * n + i] * x[i];
            hidden[j] = Math.Max(0, v);
        }
        var outputs = new float[o];
        for (int k = 0; k < o; k++)
        {
            outputs[k] = b2[k];
            for (int i = 0; i < h; i++) outputs[k] += w2[k * h + i] * hidden[i];
        }
        // The worth from the Chromatic, Ultimate and Legendary heads (kept to what can be), as RunScore weighs them.
        double chromatic = Math.Clamp(outputs[1], s.Chromatic, GameData.ChromaticCap);
        double expected = chromatic + RunScore.Worth[4] * Math.Max(0, outputs[2]) + RunScore.Worth[3] * Math.Max(0, outputs[3]);
        Assert.Equal(expected, net.Value(s), 3);
        File.Delete(path);
    }

    [Fact]
    public void Features_stay_in_range_over_runs()
    {
        var engine = new Engine();
        var x = new float[Features.Count];
        for (ulong seed = 1; seed < 30; seed++)
        {
            var s = engine.NewRun(seed);
            Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
            var p = new RandomPolicy(seed);
            while (!s.Ended)
            {
                Features.Encode(s, x);
                Assert.All(x, v => Assert.InRange(v, 0f, 1f));
                int k = engine.Legal(s, legal);
                engine.Apply(ref s, p.Choose(engine, s, legal[..k]));
            }
        }
    }
}
