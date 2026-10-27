using Tartarus.Rules;
using static Tartarus.Rules.Tests.Setup;

namespace Tartarus.Rules.Tests;

public class DayTests
{
    [Fact]
    public void Destinations_are_distinct_cells_1_to_6_ahead_nearest_first()
    {
        var e = new Engine();
        for (ulong seed = 1; seed < 500; seed++)
        {
            var s = e.NewRun(seed);
            Assert.Equal(Phase.Move, s.Phase);
            Assert.InRange(s.DestinationCount, 1, 3);
            int last = 0;
            for (int i = 0; i < s.DestinationCount; i++)
            {
                int steps = Engine.Steps(s.Position, s.Destinations[i]);
                Assert.InRange(steps, 1, 6);
                Assert.True(steps > last);
                last = steps;
            }
        }
    }

    [Fact]
    public void Barbed_Whip_Forged_Key_and_Burning_Stake_each_take_a_Destination()
    {
        var s = Empty(warden: 3);
        Put(ref s, 3, Code.Rat, Rarity.Normal);
        Hold(ref s, Instrument.Whip, Instrument.Forged);
        Do(Manual, ref s, new GameAction(ActionKind.End));
        Assert.Equal(1, s.DestinationCount);
    }

    [Fact]
    public void A_move_opens_the_Unopened_Destination_and_counts_its_tiles()
    {
        var s = Empty(warden: 3);
        s.Phase = Phase.Move;
        s.Destinations[0] = 7;
        s.DestinationCount = 1;
        Do(Manual, ref s, new GameAction(ActionKind.Move, 7));
        Assert.Equal(7, s.Position);
        Assert.True(s.Cells[7].HasInmate);
        Assert.Equal(4, s.ObjectiveProgress);
        Assert.Equal(Phase.Act, s.Phase);
    }

    [Fact]
    public void The_Interrogation_Room_offers_Instigate_or_Bestow()
    {
        var s = Empty(warden: 13);
        Put(ref s, 5, Code.Rat, Rarity.Normal);
        s.Phase = Phase.Move;
        s.Destinations[0] = 0;
        s.DestinationCount = 1;
        Do(Manual, ref s, new GameAction(ActionKind.Move, 0));
        Assert.Equal([GameAction.Instigate, GameAction.Bestow], Legal(Manual, s));
        Do(Manual, ref s, GameAction.Bestow);
        Assert.Equal(Rarity.Magic, At(s, 5).Rarity);
        Assert.Equal([new GameAction(ActionKind.End)], Legal(Manual, s));
    }

    [Fact]
    public void Instigate_places_two_markers_on_a_Sandbox_with_none_then_one()
    {
        var s = Empty(warden: 13);
        s.Phase = Phase.Move;
        s.Destinations[0] = 0;
        s.DestinationCount = 1;
        Do(Manual, ref s, new GameAction(ActionKind.Move, 0));
        Do(Manual, ref s, GameAction.Instigate);
        Assert.Equal(2, Markers(s));
    }

    [Fact]
    public void Tyranny_adds_three_Instigates_and_Bestows_per_stop()
    {
        var s = Empty(warden: 13, theme: Theme.Tyranny);
        Put(ref s, 5, Code.Rat, Rarity.Normal);
        s.Phase = Phase.Move;
        s.Destinations[0] = 0;
        s.DestinationCount = 1;
        Do(Manual, ref s, new GameAction(ActionKind.Move, 0));
        Assert.Equal(Rarity.Legendary, At(s, 5).Rarity);
        Assert.True(Markers(s) >= 3);
    }

    [Fact]
    public void Iron_Law_runs_thirty_days()
    {
        var e = new Engine(new RuleOptions { Theme = Theme.IronLaw });
        var s = e.NewRun(4);
        Assert.Equal(30, s.MaxDays);
        Runs.PlayOut(e, ref s, new RandomPolicy(4));
        Assert.True(s.Ended);
        Assert.Equal(30, s.Day);
    }

    [Fact]
    public void The_run_ends_after_the_last_day()
    {
        var s = Empty(warden: 3);
        s.Day = 20;
        Do(Manual, ref s, new GameAction(ActionKind.End));
        Assert.True(s.Ended);
        Assert.Empty(Legal(Manual, s));
    }

    [Fact]
    public void Completing_the_Objective_brings_an_instrument_pick()
    {
        var s = Empty(warden: 3);
        s.Objective = ObjectiveKind.BehindCellDoors;
        s.ObjectiveProgress = 9;
        s.Phase = Phase.Move;
        s.Destinations[0] = 5;
        s.DestinationCount = 1;
        Do(Manual, ref s, new GameAction(ActionKind.Move, 5));
        Assert.True(s.ObjectiveDone);
        Assert.Equal(PendingKind.Instrument, s.Pending[0].Kind);
        Assert.True(s.Pending[0].ForObjective);
        Assert.Equal(3, s.Pending[0].Count);
    }

    [Fact]
    public void An_offer_leaves_out_held_instruments_and_other_species()
    {
        for (ulong seed = 1; seed < 300; seed++)
        {
            var s = Empty(warden: 3, seed: seed);
            Hold(ref s, Instrument.Claws);
            s.Objective = ObjectiveKind.BehindCellDoors;
            s.ObjectiveProgress = 9;
            s.Phase = Phase.Move;
            s.Destinations[0] = 5;
            s.DestinationCount = 1;
            Do(Manual, ref s, new GameAction(ActionKind.Move, 5));
            var p = s.Pending[0];
            for (int i = 0; i < p.Count; i++)
            {
                var info = GameData.Info(p.Offer(i));
                Assert.NotEqual(Instrument.Claws, info.Instrument);
                Assert.True(info.Species is null or Code.Wolf, info.Name);
            }
        }
    }

    [Fact]
    public void Re_roll_draws_a_new_offer_and_spends_an_attempt()
    {
        var e = new Engine(new RuleOptions { Rerolls = 1, RerollPercent = 100 });
        var s = e.NewRun(9);
        Assert.Contains(new GameAction(ActionKind.Reroll), Legal(e, s));
        Do(e, ref s, new GameAction(ActionKind.Reroll));
        Assert.Equal(0, s.Rerolls);
        Assert.Equal(PendingKind.Instrument, s.Pending[0].Kind);
        Assert.DoesNotContain(new GameAction(ActionKind.Reroll), Legal(e, s));
    }

    [Fact]
    public void A_run_has_its_Re_roll_by_the_Pactspirit_s_chance()
    {
        // Twin Inquisitors: a 50% chance of 1 refresh for the run's Warden's Duty.
        var e = new Engine();
        int with = Enumerable.Range(1, 2000).Count(seed => e.NewRun((ulong)seed).Rerolls == 1);
        Assert.InRange(with, 900, 1100);
    }

    private static int Markers(in GameState s)
    {
        int k = 0;
        for (int n = 1; n < GameData.Cells; n++) if (s.Cells[n].Marker != Incident.None) k++;
        return k;
    }
}
