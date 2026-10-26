namespace Tartarus.Rules;

public sealed partial class Engine
{
    // ---------- Replace ----------

    /// <summary>Replace: the Inmate in the Warden's cell makes way for a new one, with the Replace instruments' effects.</summary>
    private void Replace(ref GameState s)
    {
        int n = s.Position;
        var old = s.Cells[n].Inmate;
        s.Replaced = true;
        s.Stats.Replaces++;

        // Severed Iron Maiden's condition, on the Sandbox as it is before anything leaves.
        Span<ushort> ids = stackalloc ushort[GameData.Cells];
        int k = BoardIds(s, ids);
        bool level = false;
        if (s.Has(Instrument.Maiden) && Options.MaidenOnReplaced) level = Level(s, ids[..k], old);

        s.Cells[n].Inmate = default;
        CheckPrey(ref s);
        Span<double> w = stackalloc double[5];
        Options.ReplaceWeights.AsSpan(0, 5).CopyTo(w);
        if (s.Has(Instrument.Noose)) w[0] = w[1] = 0;

        // Scapegoat's Guillotine: with 2 or more others on the Sandbox, 2 random ones go and the new Inmate comes in rarer.
        bool above = false;
        if (s.Has(Instrument.Scapegoat))
        {
            Span<byte> others = stackalloc byte[GameData.Cells];
            int o = 0;
            for (int c = 1; c < GameData.Cells; c++) if (s.Cells[c].HasInmate) others[o++] = (byte)c;
            if (o >= 2)
            {
                s.Rng.Shuffle(others[..o]);
                EmptyCell(ref s, others[0]);
                EmptyCell(ref s, others[1]);
                CheckPrey(ref s);
                above = true;
                for (int r = 0; r <= (int)old.Rarity && r < 5; r++) w[r] = 0;
            }
        }
        int rolled = s.Rng.Pick(w);
        var rarity = rolled >= 0 ? (Rarity)rolled : (Rarity)Math.Min((int)old.Rarity + 1, (int)Rarity.Chromatic);
        if (above && rarity <= old.Rarity) rarity = (Rarity)Math.Min((int)old.Rarity + 1, (int)Rarity.Chromatic);
        Code code;
        if (s.Has(Instrument.Pestle)) code = old.Code;
        else if (Options.ReplaceNewCode) code = (Code)(((int)old.Code + 1 + s.Rng.Below(3)) % 4);
        else code = RandomCode(ref s);
        // Burning Stake: Replacing an Inmate below Legendary may bring in a Legendary.
        if (s.Has(Instrument.Stake) && old.Rarity < Rarity.Legendary && s.Rng.Chance(40) && rarity < Rarity.Legendary) rarity = Rarity.Legendary;
        var fresh = NewInmate(ref s, code, rarity);
        s.Cells[n].Inmate = fresh;
        AfterGrowth(ref s, n);

        if (s.Has(Instrument.Maiden) && !Options.MaidenOnReplaced && s.Cells[n].HasInmate)
        {
            k = BoardIds(s, ids);
            level = Level(s, ids[..k], fresh);
        }
        if (level && s.Cells[n].HasInmate && s.Cells[n].Inmate.Id == fresh.Id) Promote(ref s, fresh.Id, 3, reroll: false);
        if (s.Has(Instrument.Scales) && s.Rng.Chance(80)) Scout(ref s);
        // Gaoler's Execution Axe: a Replace that raised the rarity opens 2 random cells.
        var now = FindCell(s, fresh.Id) is int at ? s.Cells[at].Inmate.Rarity : Rarity.Chromatic;
        if (s.Has(Instrument.Axe) && now > old.Rarity) OpenRandom(ref s, 2, How.Effect);
        // Tusk: Replacing the Inmate on a Tusk mark raises 4 random Inmates (the new one among them) a tier.
        if (s.Cells[n].Has(CellMarks.Tusk) && s.Cells[n].HasInmate && s.Cells[n].Inmate.Id == fresh.Id)
        {
            k = BoardIds(s, ids);
            s.Rng.Shuffle(ids[..k]);
            for (int i = 0; i < Math.Min(Options.TuskInmates, k); i++) Promote(ref s, ids[i], 1, reroll: false);
        }
    }

    /// <summary>As many Inmates on the Sandbox (but <paramref name="me"/>) above its rarity as below it.</summary>
    private static bool Level(in GameState s, ReadOnlySpan<ushort> ids, Inmate me)
    {
        int above = 0, below = 0;
        foreach (var id in ids)
        {
            if (id == me.Id || FindCell(s, id) is not int c) continue;
            var r = s.Cells[c].Inmate.Rarity;
            if (r > me.Rarity) above++;
            else if (r < me.Rarity) below++;
        }
        return above == below;
    }

    /// <summary>Warden's Scales: the rarity of the rarest Inmate behind the Unopened cells shows (not its Code).</summary>
    private void Scout(ref GameState s)
    {
        Span<byte> cells = stackalloc byte[GameData.Cells];
        int k = SealedCells(s, cells, noNests: true);
        if (k == 0) return;
        var top = Rarity.Normal;
        for (int i = 0; i < k; i++) top = (Rarity)Math.Max((int)top, (int)HiddenOf(ref s, cells[i]).Rarity);
        Span<byte> best = stackalloc byte[GameData.Cells];
        int b = 0;
        for (int i = 0; i < k; i++) if (s.Cells[cells[i]].Inmate.Rarity == top) best[b++] = cells[i];
        s.Cells[best[s.Rng.Below(b)]].Marks |= CellMarks.RarityShown;
    }

    // ---------- After opening ----------

    /// <summary>The "after opening" instruments, for the door a move or an effect opened (<paramref name="id"/> came out of cell <paramref name="n"/>).</summary>
    private void AfterOpening(ref GameState s, int n, ushort id)
    {
        // Felon's Dog Tag: the Inmate that came out rises a tier.
        if (s.Has(Instrument.Dogtag) && s.Rng.Chance(25)) Promote(ref s, id, 1, reroll: false);
        // Executioner's Blacklist: with no more than 5 Unopened cells (counted before this one opened), 2 random Inmates rise.
        if (s.Has(Instrument.Blacklist))
        {
            Span<byte> sealedCells = stackalloc byte[GameData.Cells];
            if (SealedCells(s, sealedCells) < 5)
            {
                Span<ushort> ids = stackalloc ushort[GameData.Cells];
                int k = BoardIds(s, ids);
                s.Rng.Shuffle(ids[..k]);
                for (int i = 0; i < Math.Min(2, k); i++) Promote(ref s, ids[i], 1, reroll: false);
            }
        }
        // Searing Branding Iron: a copy of the Inmate opens a random Unopened cell (no other effects).
        if (s.Has(Instrument.Brand) && s.Rng.Chance(35) && FindCell(s, id) is int at)
        {
            var me = s.Cells[at].Inmate;
            int target = RandomSealed(ref s);
            if (target > 0) OpenCell(ref s, target, How.Side, NewInmate(ref s, me.Code, me.Rarity));
        }
        // Lost Ring of Keys: the cells on both sides open too (no other effects).
        if (s.Has(Instrument.Keys) && s.Rng.Chance(80))
        {
            OpenCell(ref s, Ring(n, -1), How.Side);
            OpenCell(ref s, Ring(n, 1), How.Side);
        }
        // Forged Key: 1 more random cell opens (no other effects).
        if (s.Has(Instrument.Forged) && s.Rng.Chance(50)) OpenRandom(ref s, 1, How.Side);
    }

    // ---------- Mornings ----------

    private void MorningInstruments(ref GameState s)
    {
        // Iron Tongs of Lust: a random Mammoth without one gets a Tusk mark.
        if (s.Has(Instrument.Tongs))
        {
            Span<byte> m = stackalloc byte[GameData.Cells];
            int k = 0;
            for (int n = 1; n < GameData.Cells; n++)
                if (s.Cells[n].HasInmate && s.Cells[n].Inmate.Code == Code.Mammoth && !s.Cells[n].Has(CellMarks.Tusk)) m[k++] = (byte)n;
            if (k > 0) s.Cells[m[s.Rng.Below(k)]].Marks |= CellMarks.Tusk;
        }
        // Squirming Iron Box: a random Unopened cell becomes a Rat Nest.
        if (s.Has(Instrument.Ironbox))
        {
            Span<byte> free = stackalloc byte[GameData.Cells];
            int k = SealedCells(s, free, noNests: true);
            if (k > 0) s.Cells[free[s.Rng.Below(k)]].NestDue = (byte)(s.Day + Options.NestDays);
        }
        // Filthy Claws: Scratch Marks on a random Unopened cell; its Inmate's rarity shows.
        if (s.Has(Instrument.Claws))
        {
            Span<byte> free = stackalloc byte[GameData.Cells];
            int k = 0;
            for (int n = 1; n < GameData.Cells; n++)
                if (s.Cells[n].Sealed && s.Cells[n].NestDue == 0 && !s.Cells[n].Has(CellMarks.Scratch)) free[k++] = (byte)n;
            if (k > 0)
            {
                int n = free[s.Rng.Below(k)];
                HiddenOf(ref s, n);
                s.Cells[n].Marks |= CellMarks.Scratch | CellMarks.RarityShown;
            }
        }
        if (s.Has(Instrument.Coin) && s.Rng.Chance(60)) CoinOfFate(ref s);
        if (s.Has(Instrument.Chains)) RavenousChains(ref s);
    }

    /// <summary>Kin's Coin of Fate: one of a matching pair (same Code and rarity) goes, and another match rises 2 tiers.</summary>
    private void CoinOfFate(ref GameState s)
    {
        Span<byte> matched = stackalloc byte[GameData.Cells];
        int k = 0;
        for (int a = 1; a < GameData.Cells; a++)
        {
            if (!s.Cells[a].HasInmate) continue;
            for (int b = 1; b < GameData.Cells; b++)
                if (b != a && s.Cells[b].HasInmate && Same(s.Cells[a].Inmate, s.Cells[b].Inmate))
                {
                    matched[k++] = (byte)a;
                    break;
                }
        }
        if (k == 0) return;
        int gone = matched[s.Rng.Below(k)];
        var g = s.Cells[gone].Inmate;
        Span<ushort> kin = stackalloc ushort[GameData.Cells];
        int m = 0;
        for (int i = 0; i < k; i++)
            if (matched[i] != gone && Same(s.Cells[matched[i]].Inmate, g)) kin[m++] = s.Cells[matched[i]].Inmate.Id;
        ushort lucky = kin[s.Rng.Below(m)];
        EmptyCell(ref s, gone);
        CheckPrey(ref s);
        Promote(ref s, lucky, 2, reroll: false);

        static bool Same(Inmate x, Inmate y) => x.Code == y.Code && x.Rarity == y.Rarity;
    }

    /// <summary>Ferocious Beast's Ravenous Chains: the Inmates on both sides of the rarest one go; 40% per removal it rises a tier.</summary>
    private void RavenousChains(ref GameState s)
    {
        var top = Rarity.Normal;
        bool any = false;
        for (int n = 1; n < GameData.Cells; n++)
            if (s.Cells[n].HasInmate)
            {
                any = true;
                top = (Rarity)Math.Max((int)top, (int)s.Cells[n].Inmate.Rarity);
            }
        if (!any) return;
        Span<byte> tops = stackalloc byte[GameData.Cells];
        int k = 0;
        for (int n = 1; n < GameData.Cells; n++) if (s.Cells[n].HasInmate && s.Cells[n].Inmate.Rarity == top) tops[k++] = (byte)n;
        int center = tops[s.Rng.Below(k)];
        ushort id = s.Cells[center].Inmate.Id;
        int removed = 0;
        foreach (int side in (ReadOnlySpan<int>)[Ring(center, -1), Ring(center, 1)])
        {
            if (!s.Cells[side].HasInmate) continue;
            EmptyCell(ref s, side);
            removed++;
        }
        if (removed == 0) return;
        CheckPrey(ref s);
        int gain = 0;
        for (int i = 0; i < removed; i++) if (s.Rng.Chance(40)) gain++;
        if (gain > 0) Promote(ref s, id, gain, reroll: false);
    }

    /// <summary>The Shackles on a move: Short-Chained (no more than 3 tiles, 50%) opens a random cell; Long-Chained (4 or more, 40%) raises a random Inmate.</summary>
    private void Shackles(ref GameState s, int steps)
    {
        if (steps > 0 && steps <= 3 && s.Has(Instrument.Shortchain) && s.Rng.Chance(50)) OpenRandom(ref s, 1, How.Side);
        if (steps >= 4 && s.Has(Instrument.Shackles) && s.Rng.Chance(40))
        {
            Span<ushort> ids = stackalloc ushort[GameData.Cells];
            int k = BoardIds(s, ids);
            if (k > 0) Promote(ref s, ids[s.Rng.Below(k)], 1, reroll: false);
        }
    }

    // ---------- Instrument offers ----------

    /// <summary>
    /// An offer of up to <paramref name="count"/> different instruments, by the game's weights (the species ones
    /// 5 against 40), without those held and those a held species instrument keeps out.
    /// </summary>
    private static Pending DrawOffer(ref GameState s, int count)
    {
        Span<double> w = stackalloc double[GameData.InstrumentCount];
        for (int i = 0; i < GameData.InstrumentCount; i++)
        {
            var inst = (Instrument)i;
            if (s.Has(inst)) continue;
            bool barred = false;
            for (int h = 0; h < GameData.InstrumentCount; h++)
                if (s.Has((Instrument)h) && GameData.Excludes((Instrument)h, inst)) barred = true;
            if (!barred) w[i] = GameData.Info(inst).Weight;
        }
        var p = new Pending { Kind = PendingKind.Instrument };
        for (int k = 0; k < count; k++)
        {
            int i = s.Rng.Pick(w);
            if (i < 0) break;
            w[i] = 0;
            if (k == 0) p.A = (byte)i; else if (k == 1) p.B = (byte)i; else p.C = (byte)i;
            p.Count++;
        }
        return p;
    }

    private void TakeInstrument(ref GameState s, Instrument inst)
    {
        PopPending(ref s);
        s.Instruments |= 1u << (int)inst;
        // A "-1 daily destination option" instrument taken before today's move takes one Destination away now.
        if (GameData.Info(inst).FewerDestinations > 0 && s.Phase == Phase.Move && s.DestinationCount > 1)
        {
            int drop = s.Rng.Below(s.DestinationCount);
            for (int i = drop; i < s.DestinationCount - 1; i++) s.Destinations[i] = s.Destinations[i + 1];
            s.DestinationCount--;
        }
        // A later offer drawn before this pick can't give the same instrument again.
        for (int i = s.PendingCount - 1; i >= 0; i--)
        {
            ref var p = ref s.Pending[i];
            if (p.Kind != PendingKind.Instrument) continue;
            int kept = 0;
            Span<byte> o = [p.A, p.B, p.C];
            for (int j = 0; j < p.Count; j++) if (o[j] != (byte)inst) o[kept++] = o[j];
            (p.A, p.B, p.C, p.Count) = (o[0], o[1], o[2], (byte)kept);
            if (kept == 0) RemovePending(ref s, i);
        }
    }

    /// <summary>Re-roll: a new offer from the same pool (the old cards may come back); one of the run's attempts.</summary>
    private static void Reroll(ref GameState s)
    {
        s.Rerolls--;
        var old = s.Pending[0];
        var fresh = DrawOffer(ref s, old.Count);
        fresh.ForObjective = old.ForObjective;
        s.Pending[0] = fresh;
    }
}
