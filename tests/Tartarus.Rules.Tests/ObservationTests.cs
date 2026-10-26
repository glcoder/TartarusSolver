using Tartarus.Rules;

namespace Tartarus.Rules.Tests;

public class ObservationTests
{
    /// <summary>What the screen would show of a state.</summary>
    private static Observation Seen(in GameState s)
    {
        var o = new Observation
        {
            Warden = s.Position,
            RemainingDays = s.RemainingDays,
            Chromatic = s.Chromatic,
            Theme = s.Theme,
            Objective = s.Objective,
            ObjectiveProgress = s.ObjectiveProgress,
            ObjectiveDone = s.ObjectiveDone,
            Rerolls = s.Rerolls,
            ReplaceUsed = s.Replaced,
            DecisionUsed = s.Used != DayAction.None,
        };
        for (int i = 0; i < GameData.InstrumentCount; i++) if (s.Has((Instrument)i)) o.Instruments.Add((Instrument)i);
        for (int n = 1; n < GameData.Cells; n++)
        {
            ref readonly var c = ref s.Cells[n];
            o.Cells[n] = new ObservedCell
            {
                Kind = c.HasInmate ? ObservedKind.Inmate : ObservedKind.Unopened,
                Code = c.Inmate.Code,
                Rarity = c.Inmate.Rarity,
                Shown = c.Sealed && c.Has(CellMarks.RarityShown) ? c.Inmate.Rarity : null,
                Scratch = c.Has(CellMarks.Scratch),
                Nest = c.NestDue != 0,
                Tusk = c.Has(CellMarks.Tusk),
                Marker = c.Marker != Incident.None,
            };
        }
        if (s.Phase == Phase.Move) for (int i = 0; i < s.DestinationCount; i++) o.Destinations.Add(s.Destinations[i]);
        return o;
    }

    /// <summary>A state's text without what the screen can't show (the marker's incident, the day a Rat Nest opens).</summary>
    private static string Visible(in GameState s)
    {
        var text = StateText.Describe(s);
        return string.Join('\n', text.Split('\n').Select(l =>
            System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(l, " ![^|]*", " !"), @"\(day \d+\)", "")));
    }

    [Fact]
    public void A_state_made_from_what_the_screen_shows_shows_the_same()
    {
        var engine = new Engine();
        Span<GameAction> legal = stackalloc GameAction[Engine.MaxActions];
        var policy = new RandomPolicy(8);
        int compared = 0;
        for (ulong seed = 1; seed < 200; seed++)
        {
            var s = engine.NewRun(seed);
            while (!s.Ended)
            {
                if (s.PendingCount == 0 && s.PreyCount == 0)
                {
                    var made = engine.FromObservation(Seen(s), seed);
                    Assert.Equal(Visible(s), Visible(made));
                    compared++;
                }
                int k = engine.Legal(s, legal);
                engine.Apply(ref s, policy.Choose(engine, s, legal[..k]));
            }
        }
        Assert.True(compared > 1000);
    }

    [Fact]
    public void Determinize_keeps_what_is_shown_and_draws_the_rest_again()
    {
        var engine = new Engine();
        var s = engine.NewRun(12);
        Runs.PlayOut(engine, ref s, new RandomPolicy(3), maxSteps: 25);
        var copy = s;
        engine.Determinize(ref copy, 99);
        for (int n = 1; n < GameData.Cells; n++)
        {
            ref readonly var a = ref s.Cells[n];
            ref readonly var b = ref copy.Cells[n];
            Assert.Equal(a.Sealed, b.Sealed);
            if (a.HasInmate) Assert.Equal(a.Inmate.ToString(), b.Inmate.ToString());
            if (a.Sealed && a.Has(CellMarks.RarityShown)) Assert.Equal(a.Inmate.Rarity, b.Inmate.Rarity);
            if (a.Sealed && !a.Has(CellMarks.RarityShown)) Assert.True(b.Inmate.IsNone);
            Assert.Equal(a.Marker != Incident.None, b.Marker != Incident.None);
        }
    }

    [Fact]
    public void A_scanned_decision_can_be_played_on()
    {
        var o = new Observation { Warden = 4, RemainingDays = 12, Objective = ObjectiveKind.SurvivalOfTheFittest, ObjectiveProgress = 2 };
        for (int n = 1; n < GameData.Cells; n++) o.Cells[n].Kind = ObservedKind.Unopened;
        o.Cells[4] = new ObservedCell { Kind = ObservedKind.Inmate, Code = Code.Wolf, Rarity = Rarity.Rare };
        o.Cells[9] = new ObservedCell { Kind = ObservedKind.Inmate, Code = Code.Rat, Rarity = Rarity.Rare, Marker = true };
        o.Instruments.Add(Instrument.Saw);
        var engine = new Engine();
        var s = engine.FromObservation(o, 1);
        Assert.Equal(Phase.Act, s.Phase);
        Assert.Equal(8, s.Day);
        Assert.True(engine.IsLegal(s, new GameAction(ActionKind.Consume, 9)));
        Runs.PlayOut(engine, ref s, new RandomPolicy(1));
        Assert.True(s.Ended);
    }

    [Fact]
    public void A_chain_on_screen_goes_on_without_a_Replace()
    {
        // 2026-10-26: after a bite and Cancel, Make Your Decision showed Consume (the chain going on) and End, no Replace.
        var o = new Observation { Warden = 4, RemainingDays = 7, ConsumedToday = true, ChainGoesOn = true };
        for (int n = 1; n < GameData.Cells; n++) o.Cells[n].Kind = ObservedKind.Unopened;
        o.Cells[4] = new ObservedCell { Kind = ObservedKind.Inmate, Code = Code.Lion, Rarity = Rarity.Legendary };
        o.Cells[16 - 1] = new ObservedCell { Kind = ObservedKind.Inmate, Code = Code.Wolf, Rarity = Rarity.Legendary };
        var engine = new Engine();
        var s = engine.FromObservation(o, 1);
        var legal = Setup.Legal(engine, s);
        Assert.Contains(new GameAction(ActionKind.Chain, 15), legal);
        Assert.Contains(new GameAction(ActionKind.StopChain), legal);
        Assert.DoesNotContain(new GameAction(ActionKind.Replace), legal);
    }
}

public class PickObservationTests
{
    [Fact]
    public void An_Objective_offer_on_screen_becomes_the_choice_waiting()
    {
        var o = new Observation { Warden = 4, RemainingDays = 9, Objective = ObjectiveKind.BehindCellDoors, ObjectiveDone = true };
        for (int n = 1; n < GameData.Cells; n++) o.Cells[n].Kind = ObservedKind.Unopened;
        o.Cells[4] = new ObservedCell { Kind = ObservedKind.Inmate, Code = Code.Lion, Rarity = Rarity.Rare };
        o.Instruments.Add(Instrument.Coin);
        o.Offer.AddRange([Instrument.Saw, Instrument.Coin, Instrument.Axe]);
        o.OfferForObjective = true;
        var engine = new Engine();
        var s = engine.FromObservation(o, 3);
        var legal = Setup.Legal(engine, s);
        Assert.Contains(new GameAction(ActionKind.PickInstrument, (byte)Instrument.Saw), legal);
        Assert.Contains(new GameAction(ActionKind.PickInstrument, (byte)Instrument.Axe), legal);
        Assert.DoesNotContain(new GameAction(ActionKind.PickInstrument, (byte)Instrument.Coin), legal);
    }

    [Fact]
    public void The_run_s_first_offer_starts_a_run_with_it()
    {
        var o = new Observation { Theme = Theme.CageFight, Objective = ObjectiveKind.PrisonWalk, StartOfRun = true, Rerolls = 1 };
        o.Offer.AddRange([Instrument.Maiden, Instrument.Shackles, Instrument.Muzzle]);
        var engine = new Engine();
        var s = engine.FromObservation(o, 5);
        Assert.Equal(1, s.Day);
        Assert.Equal(Theme.CageFight, s.Theme);
        Assert.Equal(ObjectiveKind.PrisonWalk, s.Objective);
        Assert.Equal(Phase.Move, s.Phase);
        var legal = Setup.Legal(engine, s);
        Assert.Contains(new GameAction(ActionKind.PickInstrument, (byte)Instrument.Maiden), legal);
        Assert.Contains(new GameAction(ActionKind.Reroll), legal);
    }

    [Fact]
    public void A_Treasure_pick_on_screen_offers_the_rewards()
    {
        var o = new Observation { Warden = 4, RemainingDays = 9, Theme = Theme.Opulence, TreasurePicks = 3 };
        for (int n = 1; n < GameData.Cells; n++) o.Cells[n].Kind = ObservedKind.Unopened;
        o.Treasures.AddRange([Treasure.ThumbRing, Treasure.BrassHorn, Treasure.CrimsonVintage, Treasure.BrandOfSin]);
        var engine = new Engine();
        var s = engine.FromObservation(o, 3);
        Assert.Equal(4, Setup.Legal(engine, s).Count);
        Assert.Equal(3, s.Pending[0].Picks);
    }

    [Fact]
    public void A_missing_Consume_card_without_a_target_is_not_a_used_Consume_when_the_log_saw_none()
    {
        var engine = new Engine();
        var o = new Observation { Warden = 8, RemainingDays = 0, DecisionUsed = true };
        o.Cells[8] = new ObservedCell { Kind = ObservedKind.Inmate, Code = Code.Mammoth, Rarity = Rarity.Normal };
        o.Cells[9] = new ObservedCell { Kind = ObservedKind.Inmate, Code = Code.Mammoth, Rarity = Rarity.Normal };
        Assert.Equal(DayAction.Consume, engine.FromObservation(o, 1).Used);
        o.ConsumedToday = true;
        Assert.Equal(DayAction.Consume, engine.FromObservation(o, 1).Used);
        o.ConsumedToday = false;
        Assert.Equal(DayAction.None, engine.FromObservation(o, 1).Used);
        // With a target on the Sandbox a missing card can only mean a used Consume.
        o.Cells[10] = new ObservedCell { Kind = ObservedKind.Inmate, Code = Code.Lion, Rarity = Rarity.Normal };
        Assert.Equal(DayAction.Consume, engine.FromObservation(o, 1).Used);
    }
}
