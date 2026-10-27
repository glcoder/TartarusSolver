namespace Tartarus.Rules;

/// <summary>What a cell shows on screen.</summary>
public enum ObservedKind : byte { Unopened, Inmate, Empty }

public struct ObservedCell
{
    public ObservedKind Kind;
    public Code Code;
    public Rarity Rarity;
    /// <summary>An Unopened cell showing its Inmate's rarity.</summary>
    public Rarity? Shown;
    public bool Scratch, Nest, Tusk, Prey, Marker;
}

/// <summary>
/// What the screen shows of a game: the Sandbox and panels as the scanner reads them. Cells are indexed 0 (the
/// Interrogation Room) .. 15, screen number minus one. <see cref="Engine.FromObservation"/> makes a state of it,
/// with whatever the screen can't show drawn at random (a search re-draws it per playout: <see cref="Engine.Determinize"/>).
/// </summary>
public sealed class Observation
{
    public ObservedCell[] Cells { get; } = new ObservedCell[GameData.Cells];
    /// <summary>The Warden's cell, 0 .. 15.</summary>
    public int Warden { get; set; }
    /// <summary>Select a Destination's cards, as cells; empty once the Warden has moved (Make Your Decision).</summary>
    public List<int> Destinations { get; } = [];
    public int? RemainingDays { get; set; }
    public List<Instrument> Instruments { get; } = [];
    public int Chromatic { get; set; }
    public Theme Theme { get; set; }
    public ObjectiveKind? Objective { get; set; }
    public int ObjectiveProgress { get; set; }
    public bool ObjectiveDone { get; set; }
    public int? Rerolls { get; set; }
    /// <summary>Today's Consume (or Hunt) already taken: no Consume card on Make Your Decision.</summary>
    public bool DecisionUsed { get; set; }
    /// <summary>Whether a Consume happened today, as the game's log tells (null: unknown).</summary>
    public bool? ConsumedToday { get; set; }
    /// <summary>Today's Replace is done: Make Your Decision offers Consume (or Hunt) and End, no Replace.</summary>
    public bool ReplaceUsed { get; set; }
    /// <summary>In the Interrogation Room with Instigate and Bestow on offer.</summary>
    public bool Interrogation { get; set; }
    /// <summary>
    /// A chain is going on: today's Consume is made (the log says so) and the Warden's Inmate may eat again (the
    /// target pick's red screen, or Make Your Decision's Consume card, which then goes on with the chain).
    /// </summary>
    public bool ChainGoesOn { get; set; }
    /// <summary>The Sewer Ratway's pick is on screen: the Warden may go to any cell.</summary>
    public bool Ratway { get; set; }
    /// <summary>Today's move isn't made yet but its cards aren't on screen (a pick covers them): they're drawn.</summary>
    public bool BeforeMove { get; set; }
    /// <summary>"Select a Torture Instrument" on screen: the cards offered.</summary>
    public List<Instrument> Offer { get; } = [];
    /// <summary>The offer is the Objective's (else the run's first).</summary>
    public bool OfferForObjective { get; set; }
    /// <summary>The run's first instrument pick: the Sandbox under the cards isn't seen yet, so its first Inmates are drawn as a run starts.</summary>
    public bool StartOfRun { get; set; }
    /// <summary>A Warden's Treasure pick on screen: the rewards offered, and how many picks (Opulence: 3).</summary>
    public List<Treasure> Treasures { get; } = [];
    public int TreasurePicks { get; set; } = 1;
}

public sealed partial class Engine
{
    /// <summary>A state for what the screen shows; what it can't show (behind doors, under markers, the dice) is drawn from <paramref name="seed"/>.</summary>
    public GameState FromObservation(Observation o, ulong seed)
    {
        if (o.StartOfRun) return StartOfRun(o, seed);
        var s = new GameState { Rng = new Rng(seed), NextId = 1 };
        s.Theme = o.Theme;
        s.MaxDays = (byte)(o.Theme == Theme.IronLaw ? GameData.IronLawDays : GameData.Days);
        s.Day = (byte)Math.Clamp(s.MaxDays - (o.RemainingDays ?? s.MaxDays - 1), 1, s.MaxDays);
        s.Position = (byte)Math.Clamp(o.Warden, 0, GameData.Cells - 1);
        s.Chromatic = (byte)Math.Clamp(o.Chromatic, 0, GameData.ChromaticCap);
        s.Rerolls = (byte)Math.Clamp(o.Rerolls ?? Options.Rerolls, 0, 255);
        foreach (var i in o.Instruments) s.Instruments |= 1u << (int)i;
        s.Objective = o.Objective ?? (ObjectiveKind)s.Rng.Below(5);
        s.ObjectiveDone = o.ObjectiveDone;
        s.ObjectiveProgress = (byte)Math.Clamp(o.ObjectiveDone ? GameData.Target(s.Objective) : o.ObjectiveProgress, 0, GameData.Target(s.Objective));
        // Usurpation's stage isn't shown: the Prison Incidents start at I.

        for (int n = 0; n < GameData.Cells; n++)
        {
            ref var c = ref s.Cells[n];
            var oc = o.Cells[n];
            if (n == GameData.Room) continue;
            switch (oc.Kind)
            {
                case ObservedKind.Inmate:
                    c.Sealed = false;
                    c.Inmate = NewInmate(ref s, oc.Code, oc.Rarity);
                    if (oc.Tusk) c.Marks |= CellMarks.Tusk;
                    break;
                default:
                    // An Opened cell nobody is in reads as Unopened: the rules seal a cell its Inmate leaves.
                    c.Sealed = true;
                    if (oc.Shown is { } r)
                    {
                        c.Inmate = NewInmate(ref s, RandomCode(ref s), r);
                        c.Marks |= CellMarks.RarityShown;
                    }
                    if (oc.Scratch && oc.Shown != null) c.Marks |= CellMarks.Scratch;
                    if (oc.Nest) c.NestDue = (byte)(s.Day + 1);
                    break;
            }
        }
        for (int n = 1; n < GameData.Cells; n++)
        {
            if (!o.Cells[n].Marker) continue;
            var incident = DrawIncident(ref s, n);
            s.Cells[n].Marker = incident;
        }
        // A Prey mark's Lion isn't shown: the mark goes to a Lion that can consume it, for the mornings it may have left.
        for (int n = 1; n < GameData.Cells; n++)
        {
            if (!o.Cells[n].Prey || !s.Cells[n].HasInmate || s.PreyCount >= PreyArray.MaxPrey) continue;
            for (int l = 1; l < GameData.Cells; l++)
            {
                var lion = s.Cells[l].Inmate;
                if (l == n || !s.Cells[l].HasInmate || lion.Code != Code.Lion || !CanEat(s, lion, s.Cells[n].Inmate)) continue;
                s.Prey[s.PreyCount++] = new PreyLink { Lion = lion.Id, Prey = s.Cells[n].Inmate.Id, PreyCode = s.Cells[n].Inmate.Code, Rounds = 2, NextDay = (byte)(s.Day + 1) };
                break;
            }
        }

        if (o.Destinations.Count > 0)
        {
            s.Phase = Phase.Move;
            int k = 0;
            foreach (var d in o.Destinations.OrderBy(d => Steps(s.Position, d)))
                if (k < GameData.Destinations && d is >= 0 and < GameData.Cells) s.Destinations[k++] = (byte)d;
            s.DestinationCount = (byte)k;
        }
        else if (o.BeforeMove)
        {
            s.Phase = Phase.Move;
            RollDestinations(ref s);
        }
        else
        {
            s.Phase = Phase.Act;
            // No Consume card also when the eater has no target: then only the log tells a used day from an unused
            // one (a Replace may bring a target, and the Consume with it).
            bool used = o.DecisionUsed && !(o.ConsumedToday == false && !HasTarget(s, s.Position));
            s.Used = used ? DayAction.Consume : DayAction.None;
            s.Replaced = o.ReplaceUsed;
        }

        if (o.Interrogation && s.Phase == Phase.Act && s.Position == GameData.Room) PushPending(ref s, new Pending { Kind = PendingKind.Interrogation });
        if (o.Ratway && s.Phase == Phase.Act) PushPending(ref s, new Pending { Kind = PendingKind.Ratway });
        // A chain's next bite: the day's Consume is spent (no Replace now), the Warden's Inmate eats on or stops.
        if (o.ChainGoesOn && s.Phase == Phase.Act && s.Cells[s.Position].HasInmate && HasTarget(s, s.Position))
        {
            s.Used = DayAction.Consume;
            PushPending(ref s, new Pending { Kind = PendingKind.Chain, Eater = s.Cells[s.Position].Inmate.Id });
        }
        if (o.Treasures.Count > 0)
        {
            byte mask = 0;
            foreach (var t in o.Treasures) mask |= (byte)(1 << (int)t);
            PushPending(ref s, new Pending { Kind = PendingKind.Treasure, A = mask, Picks = (byte)Math.Clamp(o.TreasurePicks, 1, 4) });
        }
        if (o.Offer.Count > 0)
        {
            var p = new Pending { Kind = PendingKind.Instrument, ForObjective = o.OfferForObjective };
            foreach (var i in o.Offer.Where(i => !s.Has(i)).Take(GameData.InstrumentOffer))
            {
                if (p.Count == 0) p.A = (byte)i; else if (p.Count == 1) p.B = (byte)i; else p.C = (byte)i;
                p.Count++;
            }
            if (p.Count > 0) PushPending(ref s, p);
        }
        return s;
    }

    /// <summary>The run's first pick: a new run with the screen's theme, Objective and offer; its first Inmates drawn.</summary>
    private GameState StartOfRun(Observation o, ulong seed)
    {
        var e = new Engine(Options with { Rerolls = o.Rerolls ?? Options.Rerolls, RerollPercent = 100, Theme = o.Theme, Objective = o.Objective }) { AutoEnd = AutoEnd };
        var s = e.NewRun(seed);
        if (o.Offer.Count > 0 && s.PendingCount > 0 && s.Pending[0].Kind == PendingKind.Instrument)
        {
            var p = new Pending { Kind = PendingKind.Instrument };
            foreach (var i in o.Offer.Take(GameData.InstrumentOffer))
            {
                if (p.Count == 0) p.A = (byte)i; else if (p.Count == 1) p.B = (byte)i; else p.C = (byte)i;
                p.Count++;
            }
            s.Pending[0] = p;
        }
        s.Unseen = UnseenStart.Inmates | (o.Objective == null ? UnseenStart.Objective : UnseenStart.None);
        return s;
    }

    /// <summary>
    /// The run's first pick with its unseen part drawn again from <paramref name="seed"/>: other first Inmates (and
    /// Objective, when it wasn't read); the offer, the theme and the Re-rolls stay.
    /// </summary>
    private GameState RedrawStart(in GameState s, ulong seed)
    {
        var objective = (s.Unseen & UnseenStart.Objective) != 0 ? (ObjectiveKind?)null : s.Objective;
        var e = new Engine(Options with { Rerolls = s.Rerolls, RerollPercent = 100, Theme = s.Theme, Objective = objective }) { AutoEnd = AutoEnd };
        var fresh = e.NewRun(seed);
        if (s.PendingCount > 0 && fresh.PendingCount > 0) fresh.Pending[0] = s.Pending[0];
        return fresh;
    }

    /// <summary>
    /// Draws again what the player can't know, for one playout of a search: who is behind each door (a shown
    /// rarity stays, its Code is drawn), which incident each unseen marker holds, and the dice from here on. On the
    /// run's first pick, its first Inmates too (and the Objective when it wasn't read).
    /// </summary>
    /// <param name="fillHidden">
    /// Draw every door's Inmate now instead of when it opens: playouts of different moves from one determinized
    /// state then meet the same Inmates behind the same doors (fair comparison: common random numbers).
    /// </param>
    public void Determinize(ref GameState s, ulong seed, bool fillHidden = false)
    {
        if (s.Unseen != UnseenStart.None) s = RedrawStart(s, Rng.Mix(seed ^ 0x5A17A27UL));
        s.Rng = new Rng(seed);
        for (int n = 1; n < GameData.Cells; n++)
        {
            ref var c = ref s.Cells[n];
            if (c.Sealed && !c.Inmate.IsNone)
            {
                if (c.Has(CellMarks.RarityShown)) c.Inmate.Code = RandomCode(ref s);
                else c.Inmate = default;
            }
            if (fillHidden && c.Sealed && c.Inmate.IsNone && n != GameData.Room) c.Inmate = RollDoorInmate(ref s);
        }
        for (int n = 1; n < GameData.Cells; n++)
        {
            ref var c = ref s.Cells[n];
            if (c.Marker == Incident.None || c.MarkerKnown) continue;
            c.Marker = Incident.None;
            var incident = DrawIncident(ref s, n);
            s.Cells[n].Marker = incident;
        }
    }
}
