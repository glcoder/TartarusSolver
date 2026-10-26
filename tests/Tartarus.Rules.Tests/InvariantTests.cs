using Tartarus.Rules;

namespace Tartarus.Rules.Tests;

/// <summary>What must hold after every action of any run: thousands of random runs, every theme, every Objective.</summary>
public class InvariantTests
{
    public static TheoryData<int> Themes => new() { 0, 1, 2, 3, 4 };

    [Theory]
    [MemberData(nameof(Themes))]
    public void Random_runs_keep_the_rules_invariants(int theme)
    {
        var engine = new Engine(new RuleOptions { Theme = (Theme)theme, Rerolls = 2 });
        Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
        var policy = new RandomPolicy((ulong)theme + 11);
        for (ulong seed = 1; seed <= 1500; seed++)
        {
            var s = engine.NewRun(seed * 31 + (ulong)theme);
            Check(s);
            int steps = 0;
            while (!s.Ended)
            {
                int k = engine.Legal(s, legal);
                Assert.True(k > 0, StateText.Describe(s));
                var a = policy.Choose(engine, s, legal[..k]);
                Assert.True(engine.Apply(ref s, a));
                Check(s);
                Assert.True(++steps < 3000, "a run that doesn't end");
            }
            Assert.Equal(s.MaxDays, s.Day);
        }
    }

    [Fact]
    public void Illegal_actions_change_nothing()
    {
        var engine = new Engine();
        var s = engine.NewRun(5);
        var before = s;
        Assert.False(engine.Apply(ref s, new GameAction(ActionKind.Replace)));
        Assert.False(engine.Apply(ref s, new GameAction(ActionKind.Move, 0)));
        Assert.Equal(StateText.Describe(before), StateText.Describe(s));
    }

    [Fact]
    public void A_seed_replays_the_same_run_and_a_copy_is_independent()
    {
        var engine = new Engine();
        RunScore Play(ulong seed)
        {
            var s = engine.NewRun(seed);
            Runs.PlayOut(engine, ref s, new RandomPolicy(seed));
            return RunScore.Of(s);
        }
        for (ulong seed = 1; seed < 50; seed++) Assert.Equal(Play(seed), Play(seed));

        var a = engine.NewRun(3);
        var b = a;
        Runs.PlayOut(engine, ref b, new RandomPolicy(1));
        Assert.Equal(1, a.Day);
        Assert.True(b.Ended);
    }

    [Fact]
    public void A_one_step_greedy_policy_beats_random_play()
    {
        var engine = new Engine();
        double random = 0, greedy = 0;
        for (ulong seed = 1; seed <= 300; seed++)
        {
            var s = engine.NewRun(seed);
            Runs.PlayOut(engine, ref s, new RandomPolicy(seed));
            random += RunScore.Of(s).Utility;
            s = engine.NewRun(seed);
            Runs.PlayOut(engine, ref s, new GreedyPolicy(2, seed));
            greedy += RunScore.Of(s).Utility;
        }
        Assert.True(greedy > random * 2, $"greedy {greedy / 300:0.00} vs random {random / 300:0.00}");
    }

    private static void Check(in GameState s)
    {
        Assert.False(s.Cells[GameData.Room].Sealed);
        Assert.True(s.Cells[GameData.Room].Inmate.IsNone);
        Assert.Equal(Incident.None, s.Cells[GameData.Room].Marker);
        Assert.InRange(s.Chromatic, 0, GameData.ChromaticCap);
        Assert.InRange(s.Day, 1, s.MaxDays);
        Assert.InRange(s.PendingCount, 0, PendingArray.MaxPending);
        var ids = new HashSet<ushort>();
        for (int n = 1; n < GameData.Cells; n++)
        {
            ref readonly var c = ref s.Cells[n];
            if (c.HasInmate)
            {
                Assert.True(ids.Add(c.Inmate.Id), $"Inmate #{c.Inmate.Id} in two cells");
                Assert.True(c.Inmate.Rarity < Rarity.Chromatic, "a Chromatic left on the Sandbox");
                Assert.Equal(0, c.NestDue);
                Assert.False(c.Has(CellMarks.Scratch));
            }
            if (!c.Sealed) Assert.False(c.Inmate.IsNone, $"cell {n + 1} open and empty");
            if (c.Sealed) Assert.False(c.Has(CellMarks.Tusk));
            if (c.Has(CellMarks.RarityShown)) Assert.False(c.Inmate.IsNone);
        }
        for (int i = 0; i < s.PreyCount; i++)
        {
            Assert.NotNull(Engine.FindCell(s, s.Prey[i].Lion));
            Assert.NotNull(Engine.FindCell(s, s.Prey[i].Prey));
        }
        for (int i = 0; i < s.PendingCount; i++) Assert.NotEqual(PendingKind.None, s.Pending[i].Kind);
        if (s.Phase == Phase.Move && s.PendingCount == 0) Assert.True(s.DestinationCount > 0);
        if (s.Ended) Assert.Equal(0, s.PendingCount);
    }
}
