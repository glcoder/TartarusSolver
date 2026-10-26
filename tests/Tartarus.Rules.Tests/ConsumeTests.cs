using Tartarus.Rules;
using static Tartarus.Rules.Tests.Setup;

namespace Tartarus.Rules.Tests;

public class ConsumeTests
{
    private static Inmate I(Code c, Rarity r, ushort id) => new() { Id = id, Code = c, Rarity = r };

    [Fact]
    public void Counters_follow_the_rules_screen()
    {
        var s = Empty();
        Assert.True(Engine.CanEat(s, I(Code.Mammoth, Rarity.Rare, 1), I(Code.Lion, Rarity.Rare, 2)));
        Assert.True(Engine.CanEat(s, I(Code.Mammoth, Rarity.Rare, 1), I(Code.Wolf, Rarity.Rare, 2)));
        Assert.True(Engine.CanEat(s, I(Code.Lion, Rarity.Rare, 1), I(Code.Wolf, Rarity.Rare, 2)));
        Assert.True(Engine.CanEat(s, I(Code.Lion, Rarity.Rare, 1), I(Code.Rat, Rarity.Rare, 2)));
        Assert.True(Engine.CanEat(s, I(Code.Wolf, Rarity.Rare, 1), I(Code.Rat, Rarity.Rare, 2)));
        Assert.True(Engine.CanEat(s, I(Code.Rat, Rarity.Rare, 1), I(Code.Mammoth, Rarity.Rare, 2)));
        Assert.False(Engine.CanEat(s, I(Code.Rat, Rarity.Rare, 1), I(Code.Wolf, Rarity.Rare, 2)));
        Assert.False(Engine.CanEat(s, I(Code.Wolf, Rarity.Rare, 1), I(Code.Mammoth, Rarity.Rare, 2)));
        Assert.False(Engine.CanEat(s, I(Code.Mammoth, Rarity.Rare, 1), I(Code.Mammoth, Rarity.Rare, 2)));
        // Same rarity only, without instruments.
        Assert.False(Engine.CanEat(s, I(Code.Mammoth, Rarity.Rare, 1), I(Code.Lion, Rarity.Magic, 2)));
        Assert.False(Engine.CanEat(s, I(Code.Mammoth, Rarity.Rare, 1), I(Code.Lion, Rarity.Legendary, 2)));
    }

    [Fact]
    public void Species_instruments_and_Cage_Fight_widen_the_diet()
    {
        var s = Empty();
        Hold(ref s, Instrument.Guillotine);
        Assert.True(Engine.CanEat(s, I(Code.Wolf, Rarity.Rare, 1), I(Code.Rat, Rarity.Normal, 2)));
        Assert.False(Engine.CanEat(s, I(Code.Lion, Rarity.Rare, 1), I(Code.Rat, Rarity.Normal, 2)));

        s = Empty();
        Hold(ref s, Instrument.Maul);
        Assert.True(Engine.CanEat(s, I(Code.Mammoth, Rarity.Magic, 1), I(Code.Wolf, Rarity.Ultimate, 2)));

        s = Empty();
        Hold(ref s, Instrument.Crown);
        Assert.True(Engine.CanEat(s, I(Code.Lion, Rarity.Rare, 1), I(Code.Mammoth, Rarity.Rare, 2)));
        Assert.True(Engine.CanEat(s, I(Code.Lion, Rarity.Rare, 1), I(Code.Lion, Rarity.Rare, 2)));

        s = Empty();
        Hold(ref s, Instrument.Skewer);
        Assert.True(Engine.CanEat(s, I(Code.Rat, Rarity.Rare, 1), I(Code.Rat, Rarity.Rare, 2)));

        s = Empty(theme: Theme.CageFight);
        Assert.True(Engine.CanEat(s, I(Code.Lion, Rarity.Legendary, 1), I(Code.Rat, Rarity.Normal, 2)));
        Assert.False(Engine.CanEat(s, I(Code.Lion, Rarity.Normal, 1), I(Code.Rat, Rarity.Legendary, 2)));
    }

    [Fact]
    public void A_consume_raises_the_eater_rerolls_its_Code_and_seals_the_meal_cell()
    {
        var s = Empty(warden: 3);
        ushort eater = Put(ref s, 3, Code.Mammoth, Rarity.Rare);
        Put(ref s, 7, Code.Lion, Rarity.Rare);
        Assert.Contains(new GameAction(ActionKind.Consume, 7), Legal(Manual, s));
        Do(Manual, ref s, new GameAction(ActionKind.Consume, 7));
        Assert.Equal(eater, At(s, 3).Id);
        Assert.Equal(Rarity.Legendary, At(s, 3).Rarity);
        Assert.True(s.Cells[7].Sealed);
        Assert.True(s.Cells[7].Inmate.IsNone);
        Assert.Equal(DayAction.Consume, s.Used);
        Assert.Equal(1, s.Stats.Consumes);
    }

    [Fact]
    public void The_new_Code_is_drawn_from_all_four()
    {
        var seen = new HashSet<Code>();
        for (ulong seed = 1; seed < 200; seed++)
        {
            var s = Empty(warden: 3, seed: seed);
            Put(ref s, 3, Code.Mammoth, Rarity.Rare);
            Put(ref s, 7, Code.Lion, Rarity.Rare);
            Do(Manual, ref s, new GameAction(ActionKind.Consume, 7));
            seen.Add(At(s, 3).Code);
        }
        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public void Cage_Fight_keeps_the_Code()
    {
        for (ulong seed = 1; seed < 50; seed++)
        {
            var s = Empty(warden: 3, theme: Theme.CageFight, seed: seed);
            Put(ref s, 3, Code.Mammoth, Rarity.Rare);
            Put(ref s, 7, Code.Lion, Rarity.Rare);
            Do(Manual, ref s, new GameAction(ActionKind.Consume, 7));
            Assert.Equal(Code.Mammoth, At(s, 3).Code);
        }
    }

    [Fact]
    public void Reaching_Chromatic_sends_the_Inmate_to_the_panel_and_brings_a_Warden_s_Treasure()
    {
        var s = Empty(warden: 3);
        Put(ref s, 3, Code.Wolf, Rarity.Ultimate);
        Put(ref s, 9, Code.Rat, Rarity.Ultimate);
        Do(Manual, ref s, new GameAction(ActionKind.Consume, 9));
        Assert.Equal(1, s.Chromatic);
        Assert.True(s.Cells[3].Sealed);
        Assert.True(s.Cells[9].Sealed);
        Assert.Equal(1, s.PendingCount);
        Assert.Equal(PendingKind.Treasure, s.Pending[0].Kind);
        Assert.Equal(4, Legal(Manual, s).Count);
        Assert.Equal(1, s.Pending[0].Picks);
    }

    [Fact]
    public void Opulence_gives_three_picks_of_one_Treasure()
    {
        var s = Empty(warden: 3, theme: Theme.Opulence);
        Put(ref s, 3, Code.Wolf, Rarity.Ultimate);
        Put(ref s, 9, Code.Rat, Rarity.Ultimate);
        Do(Manual, ref s, new GameAction(ActionKind.Consume, 9));
        Do(Manual, ref s, new GameAction(ActionKind.PickTreasure, (byte)Treasure.ThumbRing));
        Assert.Equal(3, Legal(Manual, s).Count);
        Do(Manual, ref s, new GameAction(ActionKind.PickTreasure, (byte)Treasure.BrandOfSin));
        Assert.Equal(2, Legal(Manual, s).Count);
        Do(Manual, ref s, new GameAction(ActionKind.PickTreasure, (byte)Treasure.BrassHorn));
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public void With_ten_Chromatic_contained_Inmates_stop_at_Ultimate()
    {
        var s = Empty(warden: 3);
        s.Chromatic = 10;
        Put(ref s, 3, Code.Wolf, Rarity.Ultimate);
        Put(ref s, 9, Code.Rat, Rarity.Ultimate);
        Do(Manual, ref s, new GameAction(ActionKind.Consume, 9));
        Assert.Equal(10, s.Chromatic);
        Assert.Equal(Rarity.Ultimate, At(s, 3).Rarity);
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public void A_Consume_may_follow_a_Replace_but_not_a_second_Replace()
    {
        // Pestle keeps the Mammoth a Mammoth, Noose makes it Rare or better: the Rare Lion and Wolf around stay food for some draw.
        bool consumeOffered = false;
        for (ulong seed = 1; seed < 60; seed++)
        {
            var s = Empty(warden: 3, seed: seed);
            Hold(ref s, Instrument.Pestle);
            Put(ref s, 3, Code.Mammoth, Rarity.Rare);
            Put(ref s, 7, Code.Lion, Rarity.Rare);
            Put(ref s, 9, Code.Wolf, Rarity.Magic);
            Put(ref s, 11, Code.Wolf, Rarity.Normal);
            Do(Manual, ref s, new GameAction(ActionKind.Replace));
            var legal = Legal(Manual, s);
            Assert.DoesNotContain(new GameAction(ActionKind.Replace), legal);
            Assert.Contains(new GameAction(ActionKind.End), legal);
            if (legal.Any(a => a.Kind == ActionKind.Consume)) consumeOffered = true;
        }
        Assert.True(consumeOffered);
    }

    [Fact]
    public void A_Replace_may_come_before_the_Consume_but_not_after()
    {
        // The game's log: a Replace then a Consume on the same day, and days ending by themselves right after a
        // Consume (its chain stopped), never a Replace after one.
        var s = Empty(warden: 3);
        Put(ref s, 3, Code.Mammoth, Rarity.Rare);
        Put(ref s, 7, Code.Lion, Rarity.Rare);
        Put(ref s, 9, Code.Lion, Rarity.Legendary);
        Put(ref s, 11, Code.Wolf, Rarity.Legendary);
        var before = s;
        Do(Manual, ref s, new GameAction(ActionKind.Consume, 7));
        if (s.PendingCount > 0) Do(Manual, ref s, new GameAction(ActionKind.StopChain));
        Assert.Equal([new GameAction(ActionKind.End)], Legal(Manual, s));
        Do(Manual, ref before, new GameAction(ActionKind.Replace));
        Assert.DoesNotContain(new GameAction(ActionKind.Replace), Legal(Manual, before));
    }

    [Fact]
    public void The_day_ends_by_itself_when_only_End_is_left()
    {
        var engine = new Engine();
        var s = Empty(warden: 3);
        Put(ref s, 3, Code.Mammoth, Rarity.Rare);
        Put(ref s, 7, Code.Lion, Rarity.Rare);
        s.Replaced = true;
        Do(engine, ref s, new GameAction(ActionKind.Consume, 7));
        Assert.Equal(6, s.Day);
        Assert.Equal(Phase.Move, s.Phase);
    }

    [Fact]
    public void The_chain_goes_on_while_the_eater_has_targets_and_stops_on_Cancel()
    {
        // Lion Rare eats a Wolf Rare: it becomes Legendary with a random Code; the Legendary Rat and Lion around make
        // it likely to have a next target, whatever Code it gets.
        int chains = 0;
        for (ulong seed = 1; seed < 100; seed++)
        {
            var s = Empty(warden: 3, seed: seed);
            Put(ref s, 3, Code.Lion, Rarity.Rare);
            Put(ref s, 5, Code.Wolf, Rarity.Rare);
            Put(ref s, 8, Code.Rat, Rarity.Legendary);
            Put(ref s, 10, Code.Mammoth, Rarity.Legendary);
            Put(ref s, 12, Code.Wolf, Rarity.Legendary);
            Do(Manual, ref s, new GameAction(ActionKind.Consume, 5));
            if (s.PendingCount == 0) continue;
            chains++;
            Assert.Equal(PendingKind.Chain, s.Pending[0].Kind);
            var legal = Legal(Manual, s);
            Assert.Contains(new GameAction(ActionKind.StopChain), legal);
            Assert.All(legal.Where(a => a.Kind == ActionKind.Chain), a => Assert.True(Engine.CanEat(s, At(s, 3), At(s, a.Arg))));
            Do(Manual, ref s, new GameAction(ActionKind.StopChain));
            Assert.Equal(0, s.PendingCount);
        }
        Assert.True(chains > 50);
    }

    [Fact]
    public void Muzzle_adds_two_tiers_with_no_more_than_four_Inmates()
    {
        var s = Empty(warden: 3);
        Hold(ref s, Instrument.Muzzle);
        Put(ref s, 3, Code.Mammoth, Rarity.Normal);
        Put(ref s, 7, Code.Lion, Rarity.Normal);
        Put(ref s, 9, Code.Rat, Rarity.Rare);
        Do(Manual, ref s, new GameAction(ActionKind.Consume, 7));
        Assert.Equal(Rarity.Legendary, At(s, 3).Rarity);

        s = Empty(warden: 3);
        Hold(ref s, Instrument.Muzzle);
        Put(ref s, 3, Code.Mammoth, Rarity.Normal);
        Put(ref s, 7, Code.Lion, Rarity.Normal);
        for (int n = 9; n <= 12; n++) Put(ref s, n, Code.Rat, Rarity.Rare);
        Do(Manual, ref s, new GameAction(ActionKind.Consume, 7));
        Assert.Equal(Rarity.Magic, At(s, 3).Rarity);
    }

    [Fact]
    public void Crown_Skewer_and_Maul_add_their_tiers()
    {
        var s = Empty(warden: 3);
        Hold(ref s, Instrument.Crown);
        Put(ref s, 3, Code.Lion, Rarity.Normal);
        Put(ref s, 7, Code.Mammoth, Rarity.Normal);
        Do(Manual, ref s, new GameAction(ActionKind.Consume, 7));
        Assert.Equal(Rarity.Legendary, At(s, 3).Rarity);

        s = Empty(warden: 3);
        Hold(ref s, Instrument.Skewer);
        Put(ref s, 3, Code.Rat, Rarity.Normal);
        Put(ref s, 7, Code.Rat, Rarity.Normal);
        Do(Manual, ref s, new GameAction(ActionKind.Consume, 7));
        Assert.Equal(Rarity.Legendary, At(s, 3).Rarity);

        // Maul: a Magic Mammoth eats a Legendary Wolf and ends 2 tiers above it: Chromatic.
        s = Empty(warden: 3);
        Hold(ref s, Instrument.Maul);
        Put(ref s, 3, Code.Mammoth, Rarity.Magic);
        Put(ref s, 7, Code.Wolf, Rarity.Legendary);
        Do(Manual, ref s, new GameAction(ActionKind.Consume, 7));
        Assert.Equal(1, s.Chromatic);
    }

    [Fact]
    public void A_Wolf_consumes_through_Scratch_Marks_for_three_tiers()
    {
        var s = Empty(warden: 3);
        Put(ref s, 3, Code.Wolf, Rarity.Normal);
        Behind(ref s, 6, Code.Lion, Rarity.Normal, CellMarks.RarityShown | CellMarks.Scratch);
        Behind(ref s, 8, Code.Lion, Rarity.Magic, CellMarks.RarityShown | CellMarks.Scratch);
        var legal = Legal(Manual, s);
        Assert.Contains(new GameAction(ActionKind.Consume, 6), legal);
        Assert.DoesNotContain(new GameAction(ActionKind.Consume, 8), legal);
        Do(Manual, ref s, new GameAction(ActionKind.Consume, 6));
        Assert.Equal(Rarity.Legendary, At(s, 3).Rarity);
        Assert.True(s.Cells[6].Sealed);
        Assert.Equal(CellMarks.None, s.Cells[6].Marks);
    }

    [Fact]
    public void Oppress_cuts_the_Guillotine_Wolf_s_chance_to_rise()
    {
        int rose = 0, tries = 2000;
        for (ulong seed = 1; seed <= (ulong)tries; seed++)
        {
            var s = Empty(warden: 3, seed: seed);
            Hold(ref s, Instrument.Guillotine);
            Put(ref s, 3, Code.Wolf, Rarity.Ultimate);
            Put(ref s, 7, Code.Rat, Rarity.Normal);
            Do(Manual, ref s, new GameAction(ActionKind.Consume, 7));
            if (s.Chromatic == 1) rose++;
        }
        // 4 tiers of difference: 80%.
        Assert.InRange(rose / (double)tries, 0.76, 0.84);
    }

    [Fact]
    public void Hunt_marks_prey_and_both_rise_each_morning()
    {
        var s = Empty(warden: 3);
        Hold(ref s, Instrument.Trap);
        ushort lion = Put(ref s, 3, Code.Lion, Rarity.Normal);
        ushort rat = Put(ref s, 9, Code.Rat, Rarity.Normal);
        Put(ref s, 11, Code.Mammoth, Rarity.Rare);
        Assert.Contains(new GameAction(ActionKind.Hunt, 9), Legal(Manual, s));
        Do(Manual, ref s, new GameAction(ActionKind.Hunt, 9));
        Assert.Equal(DayAction.Hunt, s.Used);
        Do(Manual, ref s, new GameAction(ActionKind.End));
        Assert.Equal(Rarity.Magic, At(s, Engine.FindCell(s, lion)!.Value).Rarity);
        Assert.Equal(Rarity.Magic, At(s, Engine.FindCell(s, rat)!.Value).Rarity);
    }
}
