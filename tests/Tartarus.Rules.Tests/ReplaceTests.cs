using Tartarus.Rules;
using static Tartarus.Rules.Tests.Setup;

namespace Tartarus.Rules.Tests;

public class ReplaceTests
{
    private static GameState Replaced(ulong seed, Code code, Rarity rarity, params Instrument[] held)
    {
        var s = Empty(warden: 3, seed: seed);
        Hold(ref s, held);
        Put(ref s, 3, code, rarity);
        Do(Manual, ref s, new GameAction(ActionKind.Replace));
        return s;
    }

    [Fact]
    public void A_Replace_brings_in_another_Code_by_the_Replace_odds()
    {
        var count = new int[6];
        for (ulong seed = 1; seed <= 4000; seed++)
        {
            var s = Replaced(seed, Code.Wolf, Rarity.Rare);
            Assert.NotEqual(Code.Wolf, At(s, 3).Code);
            count[(int)At(s, 3).Rarity]++;
        }
        // 42 / 34 / 11.5 / 9.5 / 2.5.
        Assert.InRange(count[0] / 4000.0, 0.38, 0.46);
        Assert.InRange(count[4] / 4000.0, 0.012, 0.04);
    }

    [Fact]
    public void Noose_keeps_out_Normal_and_Magic_and_Pestle_keeps_the_Code()
    {
        for (ulong seed = 1; seed <= 300; seed++)
        {
            var s = Replaced(seed, Code.Lion, Rarity.Normal, Instrument.Noose, Instrument.Pestle);
            Assert.True(At(s, 3).Rarity >= Rarity.Rare);
            Assert.Equal(Code.Lion, At(s, 3).Code);
        }
    }

    [Fact]
    public void Scapegoat_removes_two_others_and_the_new_Inmate_is_rarer()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var s = Empty(warden: 3, seed: seed);
            Hold(ref s, Instrument.Scapegoat);
            Put(ref s, 3, Code.Lion, Rarity.Legendary);
            Put(ref s, 6, Code.Rat, Rarity.Normal);
            Put(ref s, 9, Code.Rat, Rarity.Normal);
            Put(ref s, 12, Code.Rat, Rarity.Normal);
            Do(Manual, ref s, new GameAction(ActionKind.Replace));
            Assert.Equal(Rarity.Ultimate, At(s, 3).Rarity);
            Assert.Equal(1, Engine.BoardCount(s) - 1);
        }
    }

    [Fact]
    public void Severed_Iron_Maiden_adds_three_tiers_when_the_replaced_Inmate_stood_level()
    {
        // The replaced Rare has one Inmate above it and one below: level.
        for (ulong seed = 1; seed <= 100; seed++)
        {
            var s = Empty(warden: 3, seed: seed);
            Hold(ref s, Instrument.Maiden, Instrument.Noose);
            Put(ref s, 3, Code.Lion, Rarity.Rare);
            Put(ref s, 6, Code.Rat, Rarity.Legendary);
            Put(ref s, 9, Code.Rat, Rarity.Normal);
            Do(Manual, ref s, new GameAction(ActionKind.Replace));
            // Rare or better (Noose) + 3: Chromatic or off to the panel.
            Assert.True(s.Chromatic == 1 || At(s, 3).Rarity >= Rarity.Ultimate);
        }
    }

    [Fact]
    public void Tusk_raises_four_Inmates_on_a_Replace()
    {
        var s = Empty(warden: 3, seed: 3);
        Put(ref s, 3, Code.Mammoth, Rarity.Normal);
        s.Cells[3].Marks |= CellMarks.Tusk;
        for (int n = 6; n <= 10; n++) Put(ref s, n, Code.Rat, Rarity.Normal);
        int before = Sum(s);
        Do(Manual, ref s, new GameAction(ActionKind.Replace));
        // The old Normal goes, the new one comes (Normal .. Ultimate), and 4 of the 6 Inmates rise a tier.
        Assert.InRange(Sum(s) - before, 4, 8);
    }

    [Fact]
    public void Gaoler_s_Axe_opens_two_cells_when_the_rarity_rose()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var s = Empty(warden: 3, seed: seed);
            Hold(ref s, Instrument.Axe);
            Put(ref s, 3, Code.Mammoth, Rarity.Normal);
            Do(Manual, ref s, new GameAction(ActionKind.Replace));
            int open = Engine.BoardCount(s);
            if (At(s, 3).Rarity > Rarity.Normal || s.Chromatic > 0) Assert.True(open >= 3);
            else Assert.Equal(1, open);
        }
    }

    [Fact]
    public void Warden_s_Scales_shows_the_rarest_Unopened_rarity()
    {
        int shown = 0;
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var s = Replaced(seed, Code.Wolf, Rarity.Normal, Instrument.Scales);
            int k = 0;
            var top = Rarity.Normal;
            for (int n = 1; n < GameData.Cells; n++)
                if (s.Cells[n].Sealed && !s.Cells[n].Inmate.IsNone) top = (Rarity)Math.Max((int)top, (int)s.Cells[n].Inmate.Rarity);
            for (int n = 1; n < GameData.Cells; n++)
                if (s.Cells[n].Has(CellMarks.RarityShown))
                {
                    k++;
                    Assert.Equal(top, s.Cells[n].Inmate.Rarity);
                }
            shown += k;
        }
        Assert.InRange(shown / 200.0, 0.7, 0.9);
    }

    private static int Sum(in GameState s)
    {
        int t = 0;
        for (int n = 1; n < GameData.Cells; n++) if (s.Cells[n].HasInmate) t += (int)s.Cells[n].Inmate.Rarity;
        return t;
    }
}
