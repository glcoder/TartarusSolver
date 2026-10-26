namespace Tartarus.Rules;

public sealed partial class Engine
{
    /// <summary>
    /// Whether <paramref name="eater"/> may consume <paramref name="meal"/>: a countered Code of the same rarity;
    /// with the Fanged Guillotine a Wolf also a lower rarity (Cage Fight: anyone); with the Elephant-Foot Maul a
    /// Mammoth also a higher one; with the Iron Crown a Lion any Code of its rarity; with the Skewered Rat Heads a
    /// Rat a Rat of its rarity.
    /// </summary>
    public static bool CanEat(in GameState s, Inmate eater, Inmate meal)
    {
        if (eater.IsNone || meal.IsNone || eater.Id == meal.Id || eater.Rarity >= Rarity.Chromatic) return false;
        bool counters = GameData.Counters(eater.Code, meal.Code);
        if (eater.Rarity > meal.Rarity)
            return counters && ((eater.Code == Code.Wolf && s.Has(Instrument.Guillotine)) || s.Theme == Theme.CageFight);
        if (eater.Rarity < meal.Rarity)
            return counters && eater.Code == Code.Mammoth && s.Has(Instrument.Maul);
        return counters
               || (eater.Code == Code.Lion && s.Has(Instrument.Crown))
               || (eater.Code == Code.Rat && meal.Code == Code.Rat && s.Has(Instrument.Skewer));
    }

    /// <summary>
    /// The Consumable targets of the Inmate in cell <paramref name="from"/>, in cell order: opened cells it can
    /// eat, and Unopened cells with Scratch Marks whose Inmate is a Wolf eater's rarity.
    /// </summary>
    public static int Targets(in GameState s, int from, Span<byte> into)
    {
        int k = 0;
        if (!s.Cells[from].HasInmate) return 0;
        var eater = s.Cells[from].Inmate;
        for (int n = 1; n < GameData.Cells; n++)
        {
            if (n == from) continue;
            ref readonly var c = ref s.Cells[n];
            if (!c.Sealed)
            {
                if (CanEat(s, eater, c.Inmate)) into[k++] = (byte)n;
            }
            else if (c.Has(CellMarks.Scratch) && eater.Code == Code.Wolf && !c.Inmate.IsNone && c.Inmate.Rarity == eater.Rarity && eater.Rarity < Rarity.Chromatic)
                into[k++] = (byte)n;
        }
        return k;
    }

    public static bool HasTarget(in GameState s, int from)
    {
        Span<byte> t = stackalloc byte[GameData.Cells];
        return Targets(s, from, t) > 0;
    }

    /// <summary>The day's Consume: the Inmate in the Warden's cell eats the one in <paramref name="to"/>; then its chain may go on.</summary>
    private void Consume(ref GameState s, int to)
    {
        ushort eater = s.Cells[s.Position].Inmate.Id;
        s.Used = DayAction.Consume;
        Bite(ref s, s.Position, to, chained: false);
        OfferChain(ref s, eater);
    }

    private void ChainBite(ref GameState s, int to)
    {
        var p = s.Pending[0];
        PopPending(ref s);
        if (FindCell(s, p.Eater) is not int from) return;
        Bite(ref s, from, to, chained: true);
        OfferChain(ref s, p.Eater);
    }

    /// <summary>While the eater has a target, it may Chain Consume: the choice waits behind any pick the bite earned.</summary>
    private static void OfferChain(ref GameState s, ushort eater)
    {
        if (FindCell(s, eater) is int n && HasTarget(s, n)) PushPending(ref s, new Pending { Kind = PendingKind.Chain, Eater = eater });
    }

    /// <summary>Chain choices whose eater left or has no target any more go (another pick may have changed the Sandbox).</summary>
    private static void RefreshChains(ref GameState s)
    {
        for (int i = s.PendingCount - 1; i >= 0; i--)
        {
            if (s.Pending[i].Kind != PendingKind.Chain) continue;
            if (FindCell(s, s.Pending[i].Eater) is not int n || !HasTarget(s, n)) RemovePending(ref s, i);
        }
    }

    /// <summary>
    /// One bite: +1 tier (through Scratch Marks +3), the instruments' extra tiers, a new random Code (not under Cage
    /// Fight); the meal's cell is Unopened again. Oppress (Fanged Guillotine, a lower meal): 5% less chance per tier
    /// of difference that the eater rises at all.
    /// </summary>
    private void Bite(ref GameState s, int from, int to, bool chained)
    {
        var eater = s.Cells[from].Inmate;
        ref var mc = ref s.Cells[to];
        bool scratch = mc.Sealed;
        var meal = mc.Inmate;
        if (eater.IsNone || meal.IsNone) return;
        int crowd = BoardCount(s);
        int gain = scratch ? 3 : 1;
        if (s.Has(Instrument.Maul) && eater.Code == Code.Mammoth)
            gain = Math.Max(gain, (int)meal.Rarity + 2 - (int)eater.Rarity);
        if (s.Has(Instrument.Muzzle) && (Options.MuzzleCountsMeal ? crowd : crowd - 1) <= 4) gain += 2;
        if (s.Has(Instrument.Skewer) && eater.Code == Code.Rat) gain += 2;
        if (s.Has(Instrument.Crown) && eater.Code == Code.Lion) gain += 2;
        EmptyCell(ref s, to);
        CheckPrey(ref s);

        int diff = eater.Rarity - meal.Rarity;
        bool oppressed = diff > 0 && eater.Code == Code.Wolf && s.Has(Instrument.Guillotine);
        if (oppressed && s.Rng.NextDouble() >= Math.Max(0, 100 - 5 * diff) / 100.0) gain = 0;
        else
        {
            if (s.Has(Instrument.Saw) && s.Rng.Chance(25)) gain++;
            if (s.Has(Instrument.Whip) && s.Rng.Chance(35)) gain++;
            if (chained && s.Has(Instrument.Collar) && s.Rng.Chance(80)) gain++;
        }
        bool casket = s.Has(Instrument.Casket) && s.Rng.Chance(30);
        bool cage = s.Has(Instrument.Cage) && s.Rng.Chance(40);
        int at = Promote(ref s, eater.Id, gain, reroll: s.Theme != Theme.CageFight);
        s.Stats.Consumes++;
        AdvanceObjective(ref s, ObjectiveKind.SurvivalOfTheFittest, 1);
        CheckPrey(ref s);
        // Crowded Beastcage: the meal's cell opens again at once (no other effects).
        if (cage) OpenCell(ref s, to, How.Side);
        // Avenger's Casket: a random cell opens on a Consumable target for the eater (no other effects).
        if (casket && at >= 0 && FindCell(s, eater.Id) is int n)
        {
            int target = RandomSealed(ref s);
            if (target > 0)
            {
                var me = s.Cells[n].Inmate;
                var prey = GameData.Prey(me.Code);
                OpenCell(ref s, target, How.Side, NewInmate(ref s, prey[s.Rng.Below(prey.Length)], me.Rarity));
            }
        }
    }

    /// <summary>Hunt (Blood-Stained Trap): the Lion in the Warden's cell may mark one of its opened Consumable targets without a mark.</summary>
    public static int HuntTargets(in GameState s, Span<byte> into)
    {
        if (!s.Has(Instrument.Trap) || s.Used != DayAction.None) return 0;
        ref readonly var here = ref s.Cells[s.Position];
        if (!here.HasInmate || here.Inmate.Code != Code.Lion || Linked(s, here.Inmate.Id) || s.PreyCount >= PreyArray.MaxPrey) return 0;
        Span<byte> t = stackalloc byte[GameData.Cells];
        int n = Targets(s, s.Position, t), k = 0;
        for (int i = 0; i < n; i++)
            if (!s.Cells[t[i]].Sealed && !Linked(s, s.Cells[t[i]].Inmate.Id)) into[k++] = t[i];
        return k;
    }

    private static bool Linked(in GameState s, ushort id)
    {
        for (int i = 0; i < s.PreyCount; i++) if (s.Prey[i].Lion == id || s.Prey[i].Prey == id) return true;
        return false;
    }

    private void Hunt(ref GameState s, int cell)
    {
        var lion = s.Cells[s.Position].Inmate;
        var prey = s.Cells[cell].Inmate;
        s.Prey[s.PreyCount++] = new PreyLink { Lion = lion.Id, Prey = prey.Id, PreyCode = prey.Code, Rounds = (byte)Options.PreyRounds, NextDay = (byte)(s.Day + 1) };
        s.Used = DayAction.Hunt;
        s.Stats.Hunts++;
    }
}
