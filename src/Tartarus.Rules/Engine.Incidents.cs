namespace Tartarus.Rules;

public sealed partial class Engine
{
    // ---------- Markers ----------

    /// <summary>Whether a marker of <paramref name="incident"/> can sit on cell <paramref name="n"/> (never the Interrogation Room).</summary>
    private static bool Fits(in GameState s, Incident incident, int n)
    {
        if (n == GameData.Room) return false;
        ref readonly var c = ref s.Cells[n];
        return GameData.Info(incident).Spawn switch
        {
            Spawn.Unopened => c.Sealed && c.NestDue == 0,
            Spawn.Opened => c.HasInmate,
            _ => c.Sealed ? c.NestDue == 0 : c.HasInmate,
        };
    }

    /// <summary>An incident's draw weight now: Usurpation's stages take turns; Taste of Freedom stays out while the Muzzle is held.</summary>
    private static double IncidentWeight(in GameState s, Incident i)
    {
        if (i == Incident.None) return 0;
        if (i is >= Incident.Usurpation1 and <= Incident.Usurpation4)
            return s.UsurpationStage == i - Incident.Usurpation1 ? GameData.Info(Incident.Usurpation1).Weight : 0;
        if (i == Incident.TasteOfFreedom && s.Has(Instrument.Muzzle)) return 0;
        return GameData.Info(i).Weight;
    }

    /// <summary>An incident for a marker on cell <paramref name="n"/>, drawn by weight among those that fit it.</summary>
    private static Incident DrawIncident(ref GameState s, int n)
    {
        Span<double> w = stackalloc double[GameData.IncidentCount];
        for (int i = 1; i < GameData.IncidentCount; i++)
            if (Fits(s, (Incident)i, n)) w[i] = IncidentWeight(s, (Incident)i);
        int pick = s.Rng.Pick(w);
        return pick < 0 ? Incident.None : (Incident)pick;
    }

    /// <summary>Places up to <paramref name="count"/> markers on random free cells (an Unopened cell or an Inmate's, without one).</summary>
    private static void AddMarkers(ref GameState s, int count, bool unopenedOnly, bool avoidWarden)
    {
        Span<byte> free = stackalloc byte[GameData.Cells];
        int k = 0;
        for (int n = 1; n < GameData.Cells; n++)
        {
            ref readonly var c = ref s.Cells[n];
            if (c.Marker != Incident.None || (avoidWarden && n == s.Position)) continue;
            if (c.Sealed ? c.NestDue == 0 : (!unopenedOnly && c.HasInmate)) free[k++] = (byte)n;
        }
        s.Rng.Shuffle(free[..k]);
        for (int i = 0; i < Math.Min(count, k); i++)
        {
            var incident = DrawIncident(ref s, free[i]);
            if (incident == Incident.None) continue;
            s.Cells[free[i]].Marker = incident;
            s.Cells[free[i]].MarkerKnown = false;
        }
    }

    /// <summary>Markers whose cell no longer fits their incident go (an Opened-cell incident whose Inmate was consumed, ...).</summary>
    private static void DropUnfitMarkers(ref GameState s)
    {
        for (int n = 1; n < GameData.Cells; n++)
        {
            var m = s.Cells[n].Marker;
            if (m != Incident.None && !Fits(s, m, n))
            {
                s.Cells[n].Marker = Incident.None;
                s.Cells[n].MarkerKnown = false;
            }
        }
    }

    /// <summary>Tops the Sandbox's markers up to <see cref="RuleOptions.MinIncidentMarkers"/>.</summary>
    private void KeepMinMarkers(ref GameState s)
    {
        int have = 0;
        for (int n = 1; n < GameData.Cells; n++) if (s.Cells[n].Marker != Incident.None) have++;
        if (have < Options.MinIncidentMarkers) AddMarkers(ref s, Options.MinIncidentMarkers - have, unopenedOnly: false, avoidWarden: true);
    }

    // ---------- Incidents ----------

    /// <summary>The Warden landed on a marker on cell <paramref name="n"/>: its incident plays out; a new marker likely follows.</summary>
    private void FireIncident(ref GameState s, Incident incident, int n)
    {
        s.Stats.Incidents++;
        Effect(ref s, incident, n);
        AdvanceObjective(ref s, ObjectiveKind.Undercurrents, 1);
        if (s.Rng.Chance(Options.IncidentFollowPercent)) AddMarkers(ref s, 1, unopenedOnly: false, avoidWarden: true);
    }

    private void Effect(ref GameState s, Incident incident, int n)
    {
        var here = s.Cells[n].HasInmate ? s.Cells[n].Inmate : default;
        switch (incident)
        {
            case Incident.HighRiskConvict:
            {
                // A random cell opens with a Legendary or rarer Inmate (no other effects).
                int cell = RandomSealed(ref s);
                if (cell < 0) break;
                var h = HiddenOf(ref s, cell);
                var rarity = h.Rarity;
                if (rarity < Rarity.Legendary)
                {
                    Span<double> w = stackalloc double[5];
                    DoorWeights(s, w);
                    w[0] = w[1] = w[2] = 0;
                    int r = s.Rng.Pick(w);
                    rarity = r < 0 ? Rarity.Legendary : (Rarity)r;
                }
                OpenCell(ref s, cell, How.Side, NewInmate(ref s, h.Code, rarity));
                break;
            }
            case Incident.TasteOfFreedom:
                OpenRandom(ref s, 3, How.Side);
                break;
            case Incident.BehindClosedDoors:
            {
                // The Inmate here takes the scarcest Code (ties drawn) and Ultimate (no other effects).
                if (here.IsNone) break;
                Span<int> count = stackalloc int[4];
                for (int c = 1; c < GameData.Cells; c++) if (s.Cells[c].HasInmate) count[(int)s.Cells[c].Inmate.Code]++;
                int fewest = int.MaxValue;
                foreach (int x in count) fewest = Math.Min(fewest, x);
                Span<byte> pool = stackalloc byte[4];
                int p = 0;
                for (int code = 0; code < 4; code++) if (count[code] == fewest) pool[p++] = (byte)code;
                s.Cells[n].Inmate.Code = (Code)pool[s.Rng.Below(p)];
                s.Cells[n].Inmate.Rarity = (Rarity)Math.Min((int)Rarity.Ultimate, (int)s.TopRarity);
                CheckPrey(ref s);
                AfterGrowth(ref s, n);
                break;
            }
            case Incident.RavenousAppetite:
            {
                // A random lowest-rarity Inmate: 5 tries at 50% to rise a tier.
                Span<ushort> low = stackalloc ushort[GameData.Cells];
                int k = 0;
                var min = Rarity.Chromatic;
                for (int c = 1; c < GameData.Cells; c++) if (s.Cells[c].HasInmate && s.Cells[c].Inmate.Rarity < min) min = s.Cells[c].Inmate.Rarity;
                for (int c = 1; c < GameData.Cells; c++) if (s.Cells[c].HasInmate && s.Cells[c].Inmate.Rarity == min) low[k++] = s.Cells[c].Inmate.Id;
                if (k == 0) break;
                ushort who = low[s.Rng.Below(k)];
                int rose = 0;
                for (int i = 0; i < 5; i++) if (s.Rng.Chance(50)) rose++;
                if (rose > 0) Promote(ref s, who, rose, reroll: false);
                break;
            }
            case Incident.ExtraPortion:
                RaiseRandom(ref s, 3, 1);
                break;
            case Incident.SharedFortune:
            {
                // The Inmate here rises a tier per other Inmate with its Code.
                if (here.IsNone) break;
                int same = 0;
                for (int c = 1; c < GameData.Cells; c++) if (c != n && s.Cells[c].HasInmate && s.Cells[c].Inmate.Code == here.Code) same++;
                if (same > 0) Promote(ref s, here.Id, same, reroll: false);
                break;
            }
            case Incident.Archenemy:
            {
                // A random cell opens on a Consumable target for the Inmate here (its rarity, a Code it counters).
                if (here.IsNone) break;
                int cell = RandomSealed(ref s);
                if (cell < 0) break;
                var prey = GameData.Prey(here.Code);
                OpenCell(ref s, cell, How.Effect, NewInmate(ref s, prey[s.Rng.Below(prey.Length)], here.Rarity));
                break;
            }
            case Incident.ExecutionEfficiency:
            {
                // Every lower-rarity Inmate the one here counters goes; it rises a tier per removal.
                if (here.IsNone) break;
                int removed = 0;
                for (int c = 1; c < GameData.Cells; c++)
                {
                    if (c == n || !s.Cells[c].HasInmate) continue;
                    var v = s.Cells[c].Inmate;
                    if (v.Rarity < here.Rarity && GameData.Counters(here.Code, v.Code))
                    {
                        EmptyCell(ref s, c);
                        removed++;
                    }
                }
                CheckPrey(ref s);
                if (removed > 0) Promote(ref s, here.Id, removed, reroll: false);
                break;
            }
            case Incident.CrowWhispers:
            {
                // The rarity of the next 4 Unopened cells clockwise shows (Rat Nests skipped).
                int shown = 0;
                for (int k = 1; k < GameData.Cells && shown < 4; k++)
                {
                    int c = (s.Position + k) % GameData.Cells;
                    if (!s.Cells[c].Sealed || c == GameData.Room || s.Cells[c].NestDue != 0) continue;
                    HiddenOf(ref s, c);
                    s.Cells[c].Marks |= CellMarks.RarityShown;
                    shown++;
                }
                break;
            }
            case Incident.TreasureHunt:
            {
                // Every Unopened cell with an Inmate below Rare shows its rarity.
                for (int c = 1; c < GameData.Cells; c++)
                {
                    if (!s.Cells[c].Sealed || s.Cells[c].NestDue != 0) continue;
                    if (HiddenOf(ref s, c).Rarity < Rarity.Rare) s.Cells[c].Marks |= CellMarks.RarityShown;
                }
                break;
            }
            case Incident.SewerRatway:
                PushPending(ref s, new Pending { Kind = PendingKind.Ratway });
                break;
            case Incident.ChainReaction:
                s.ChainReaction = true;
                break;
            case Incident.TransferOfConvicts:
            {
                // The Inmate here is copied into a random Unopened cell, which opens.
                if (here.IsNone) break;
                int cell = RandomSealed(ref s);
                if (cell > 0) OpenCell(ref s, cell, How.Effect, NewInmate(ref s, here.Code, here.Rarity));
                break;
            }
            case Incident.Usurpation1: Usurp(ref s, incident, 1, 1); break;
            case Incident.Usurpation2: Usurp(ref s, incident, 1, 2); break;
            case Incident.Usurpation3: Usurp(ref s, incident, 2, 2); break;
            case Incident.Usurpation4: Usurp(ref s, incident, int.MaxValue, 2); break;
        }
    }

    /// <summary>Up to <paramref name="count"/> random Inmates rise <paramref name="by"/> tiers.</summary>
    private void RaiseRandom(ref GameState s, int count, int by)
    {
        Span<ushort> ids = stackalloc ushort[GameData.Cells];
        int k = BoardIds(s, ids);
        s.Rng.Shuffle(ids[..k]);
        for (int i = 0; i < Math.Min(count, k); i++) Promote(ref s, ids[i], by, reroll: false);
    }

    /// <summary>Usurpation I-IV: random Inmates rise; the next stage takes this one's place in the Prison Incidents, markers too.</summary>
    private void Usurp(ref GameState s, Incident stage, int count, int by)
    {
        RaiseRandom(ref s, count, by);
        int next = stage - Incident.Usurpation1 + 1;
        s.UsurpationStage = (byte)Math.Max(s.UsurpationStage, next);
        var nextIncident = next <= 3 ? (Incident)((int)Incident.Usurpation1 + next) : Incident.None;
        for (int c = 1; c < GameData.Cells; c++)
            if (s.Cells[c].Marker == stage)
            {
                s.Cells[c].Marker = nextIncident;
                if (nextIncident == Incident.None) s.Cells[c].MarkerKnown = false;
            }
    }

    // ---------- Warden's Treasures ----------

    private void TakeTreasure(ref GameState s, Treasure t)
    {
        ref var p = ref s.Pending[0];
        p.A = (byte)(p.A & ~(1 << (int)t));
        p.Picks--;
        if (p.Picks == 0 || p.A == 0) PopPending(ref s);
        switch (t)
        {
            case Treasure.ThumbRing:
                OpenRandom(ref s, 3, How.Side);
                break;
            case Treasure.BrassHorn:
            {
                // Every Inmate with a Consumable target consumes one at random, in turn.
                Span<ushort> eaters = stackalloc ushort[GameData.Cells];
                int k = 0;
                for (int n = 1; n < GameData.Cells; n++) if (s.Cells[n].HasInmate && HasTarget(s, n)) eaters[k++] = s.Cells[n].Inmate.Id;
                Span<byte> targets = stackalloc byte[GameData.Cells];
                for (int i = 0; i < k; i++)
                {
                    if (FindCell(s, eaters[i]) is not int from) continue;
                    int tk = 0;
                    int all = Targets(s, from, targets);
                    for (int j = 0; j < all; j++) if (!s.Cells[targets[j]].Sealed) targets[tk++] = targets[j];
                    if (tk > 0) Bite(ref s, from, targets[s.Rng.Below(tk)], chained: false);
                }
                break;
            }
            case Treasure.CrimsonVintage:
            {
                // Every Inmate below Legendary rises a tier.
                Span<ushort> ids = stackalloc ushort[GameData.Cells];
                int k = 0;
                for (int n = 1; n < GameData.Cells; n++) if (s.Cells[n].HasInmate && s.Cells[n].Inmate.Rarity < Rarity.Legendary) ids[k++] = s.Cells[n].Inmate.Id;
                for (int i = 0; i < k; i++) Promote(ref s, ids[i], 1, reroll: false);
                break;
            }
            case Treasure.BrandOfSin:
            {
                // Up to 4 random Inmates without a Consumable target take a Code that has one.
                Span<ushort> hungry = stackalloc ushort[GameData.Cells];
                int k = 0;
                for (int n = 1; n < GameData.Cells; n++) if (s.Cells[n].HasInmate && !HasTarget(s, n)) hungry[k++] = s.Cells[n].Inmate.Id;
                s.Rng.Shuffle(hungry[..k]);
                Span<byte> codes = stackalloc byte[4];
                for (int i = 0; i < Math.Min(4, k); i++)
                {
                    if (FindCell(s, hungry[i]) is not int n || HasTarget(s, n)) continue;
                    var me = s.Cells[n].Inmate;
                    int c = 0;
                    for (int code = 0; code < 4; code++)
                    {
                        s.Cells[n].Inmate.Code = (Code)code;
                        if (HasTarget(s, n)) codes[c++] = (byte)code;
                    }
                    s.Cells[n].Inmate.Code = c > 0 ? (Code)codes[s.Rng.Below(c)] : me.Code;
                }
                CheckPrey(ref s);
                break;
            }
        }
    }
}
