using Tartarus.Rules;
using static Tartarus.Rules.Tests.Setup;

namespace Tartarus.Rules.Tests;

public class IncidentTests
{
    /// <summary>The Warden on cell 3 moves onto cell <paramref name="to"/>, which holds a marker of <paramref name="incident"/>.</summary>
    private static GameState LandOn(ref GameState s, int to, Incident incident)
    {
        s.Cells[to].Marker = incident;
        s.Phase = Phase.Move;
        s.Destinations[0] = (byte)to;
        s.DestinationCount = 1;
        Do(Manual, ref s, new GameAction(ActionKind.Move, (byte)to));
        return s;
    }

    [Fact]
    public void Execution_Efficiency_removes_lower_countered_Inmates_and_raises_the_executioner()
    {
        var s = Empty(warden: 3);
        Put(ref s, 7, Code.Mammoth, Rarity.Rare);
        Put(ref s, 9, Code.Lion, Rarity.Magic);
        Put(ref s, 11, Code.Wolf, Rarity.Normal);
        Put(ref s, 13, Code.Rat, Rarity.Normal);
        Put(ref s, 15, Code.Lion, Rarity.Rare);
        LandOn(ref s, 7, Incident.ExecutionEfficiency);
        Assert.Equal(Rarity.Ultimate, At(s, 7).Rarity);
        Assert.Equal(Code.Mammoth, At(s, 7).Code);
        Assert.True(s.Cells[9].Sealed);
        Assert.True(s.Cells[11].Sealed);
        Assert.True(s.Cells[13].HasInmate);
        Assert.True(s.Cells[15].HasInmate);
        Assert.Equal(1, s.Stats.Incidents);
    }

    [Fact]
    public void Behind_Closed_Doors_makes_the_opened_Inmate_an_Ultimate_of_the_scarcest_Code()
    {
        var s = Empty(warden: 3);
        Put(ref s, 9, Code.Lion, Rarity.Normal);
        Put(ref s, 11, Code.Wolf, Rarity.Normal);
        Put(ref s, 13, Code.Rat, Rarity.Normal);
        Put(ref s, 14, Code.Rat, Rarity.Normal);
        Put(ref s, 15, Code.Lion, Rarity.Normal);
        Put(ref s, 2, Code.Wolf, Rarity.Normal);
        Behind(ref s, 6, Code.Lion, Rarity.Normal, CellMarks.None);
        LandOn(ref s, 6, Incident.BehindClosedDoors);
        Assert.Equal(Rarity.Ultimate, At(s, 6).Rarity);
        Assert.Equal(Code.Mammoth, At(s, 6).Code);
    }

    [Fact]
    public void Shared_Fortune_raises_per_Inmate_of_the_same_Code()
    {
        var s = Empty(warden: 3);
        Put(ref s, 6, Code.Rat, Rarity.Normal);
        Put(ref s, 9, Code.Rat, Rarity.Magic);
        Put(ref s, 11, Code.Rat, Rarity.Rare);
        Put(ref s, 13, Code.Wolf, Rarity.Rare);
        LandOn(ref s, 6, Incident.SharedFortune);
        Assert.Equal(Rarity.Rare, At(s, 6).Rarity);
    }

    [Fact]
    public void Archenemy_opens_a_Consumable_target()
    {
        for (ulong seed = 1; seed < 100; seed++)
        {
            var s = Empty(warden: 3, seed: seed);
            Put(ref s, 6, Code.Wolf, Rarity.Legendary);
            LandOn(ref s, 6, Incident.Archenemy);
            Assert.Equal(2, Engine.BoardCount(s) + (s.Chromatic > 0 ? 1 : 0));
            for (int n = 1; n < GameData.Cells; n++)
                if (n != 6 && s.Cells[n].HasInmate) Assert.True(Engine.CanEat(s, At(s, 6), At(s, n)));
        }
    }

    [Fact]
    public void Chain_Reaction_opens_every_Unopened_cell_on_the_next_move()
    {
        var s = Empty(warden: 3);
        Put(ref s, 6, Code.Rat, Rarity.Normal);
        LandOn(ref s, 6, Incident.ChainReaction);
        Assert.True(s.ChainReaction);
        Do(Manual, ref s, new GameAction(ActionKind.End));
        int to = s.Destinations[s.DestinationCount - 1];
        Do(Manual, ref s, new GameAction(ActionKind.Move, (byte)to));
        for (int k = 1; k <= Engine.Steps(6, to); k++)
        {
            int n = (6 + k) % GameData.Cells;
            if (n != GameData.Room) Assert.False(s.Cells[n].Sealed);
        }
        Assert.False(s.ChainReaction);
    }

    [Fact]
    public void Sewer_Ratway_moves_the_Warden_to_a_picked_cell()
    {
        var s = Empty(warden: 3);
        Put(ref s, 6, Code.Rat, Rarity.Normal);
        LandOn(ref s, 6, Incident.SewerRatway);
        var legal = Legal(Manual, s);
        Assert.Equal(15, legal.Count);
        Assert.DoesNotContain(new GameAction(ActionKind.Ratway, 0), legal);
        Do(Manual, ref s, new GameAction(ActionKind.Ratway, 12));
        Assert.Equal(12, s.Position);
        Assert.True(s.Cells[12].HasInmate);
    }

    [Fact]
    public void Usurpation_climbs_its_stages()
    {
        var s = Empty(warden: 3);
        Put(ref s, 6, Code.Rat, Rarity.Normal);
        Put(ref s, 9, Code.Wolf, Rarity.Normal);
        s.Cells[11].Marker = Incident.Usurpation1;
        LandOn(ref s, 6, Incident.Usurpation1);
        Assert.Equal(1, s.UsurpationStage);
        Assert.Equal(Incident.Usurpation2, s.Cells[11].Marker);
    }

    [Fact]
    public void Treasure_Hunt_shows_rarities_below_Rare()
    {
        var s = Empty(warden: 3);
        Put(ref s, 6, Code.Rat, Rarity.Normal);
        LandOn(ref s, 6, Incident.TreasureHunt);
        for (int n = 1; n < GameData.Cells; n++)
        {
            if (!s.Cells[n].Sealed) continue;
            Assert.Equal(s.Cells[n].Inmate.Rarity < Rarity.Rare, s.Cells[n].Has(CellMarks.RarityShown));
        }
    }

    [Fact]
    public void Markers_fit_their_cells()
    {
        var e = new Engine();
        Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
        var policy = new RandomPolicy(2);
        for (ulong seed = 1; seed < 300; seed++)
        {
            var s = e.NewRun(seed);
            while (!s.Ended)
            {
                for (int n = 1; n < GameData.Cells; n++)
                {
                    var m = s.Cells[n].Marker;
                    if (m == Incident.None) continue;
                    var spawn = GameData.Info(m).Spawn;
                    if (spawn == Spawn.Unopened) Assert.True(s.Cells[n].Sealed);
                    if (spawn == Spawn.Opened) Assert.True(s.Cells[n].HasInmate);
                }
                int k = e.Legal(s, legal);
                e.Apply(ref s, policy.Choose(e, s, legal[..k]));
            }
        }
    }
}
