namespace Tartarus.Rules;

/// <summary>
/// The rules of Tartarus Prison. States are values (<see cref="GameState"/>); the engine holds only the options, so
/// one engine serves any number of states and threads. Every chance draws from the state's own <see cref="Rng"/>.
/// </summary>
public sealed partial class Engine(RuleOptions? options = null)
{
    public RuleOptions Options { get; } = options ?? RuleOptions.Default;

    /// <summary>When the only thing left to do today is End, do it at once (no decision is lost).</summary>
    public bool AutoEnd { get; init; } = true;

    /// <summary>Enough room for any state's legal actions.</summary>
    public const int MaxActions = 40;

    // ---------- A new run ----------

    /// <summary>A run as the game starts it: Inmates out on random cells, maybe a Prison Incident, the first instrument offer.</summary>
    public GameState NewRun(ulong seed)
    {
        var s = new GameState { Rng = new Rng(seed), NextId = 1, Rerolls = (byte)Math.Clamp(Options.Rerolls, 0, 255) };
        for (int n = 0; n < GameData.Cells; n++) s.Cells[n].Sealed = n != GameData.Room;

        s.Objective = Options.Objective ?? (ObjectiveKind)s.Rng.Below(5);
        s.Theme = Options.Theme ?? (s.Rng.Chance(Options.ThemePercent) ? (Theme)(1 + s.Rng.Below(4)) : Theme.None);
        s.MaxDays = (byte)(s.Theme == Theme.IronLaw ? GameData.IronLawDays : GameData.Days);
        s.Day = 1;

        int start = Options.StartInmates - (s.Rng.Chance(Options.StartFewerPercent) ? 1 : 0);
        Span<byte> cells = stackalloc byte[GameData.Cells - 1];
        for (int i = 0; i < cells.Length; i++) cells[i] = (byte)(i + 1);
        s.Rng.Shuffle(cells);
        for (int i = 0; i < Math.Min(start, cells.Length); i++)
        {
            var rarity = (Rarity)s.Rng.Pick(Options.StartWeights);
            OpenCell(ref s, cells[i], How.Start, NewInmate(ref s, RandomCode(ref s), rarity));
        }
        if (s.Rng.Chance(Options.IncidentStartPercent)) AddMarkers(ref s, 1, unopenedOnly: true, avoidWarden: true);

        var offer = DrawOffer(ref s, GameData.InstrumentOffer);
        if (offer.Count > 0) PushPending(ref s, offer, front: true);
        BeginDay(ref s);
        return s;
    }

    // ---------- What the player can do ----------

    /// <summary>The legal actions now, into <paramref name="into"/> (at least <see cref="MaxActions"/> long). Returns how many.</summary>
    public int Legal(in GameState s, Span<GameAction> into)
    {
        int k = 0;
        if (s.Phase == Phase.Ended) return 0;
        if (s.PendingCount > 0)
        {
            var p = s.Pending[0];
            switch (p.Kind)
            {
                case PendingKind.Instrument:
                    for (int i = 0; i < p.Count; i++) into[k++] = new GameAction(ActionKind.PickInstrument, (byte)p.Offer(i));
                    if (s.Rerolls > 0) into[k++] = new GameAction(ActionKind.Reroll);
                    break;
                case PendingKind.Treasure:
                    for (int t = 0; t < 4; t++)
                        if ((p.A & (1 << t)) != 0) into[k++] = new GameAction(ActionKind.PickTreasure, (byte)t);
                    break;
                case PendingKind.Interrogation:
                    into[k++] = GameAction.Instigate;
                    into[k++] = GameAction.Bestow;
                    break;
                case PendingKind.Chain:
                    if (FindCell(s, p.Eater) is int from)
                    {
                        Span<byte> targets = stackalloc byte[GameData.Cells];
                        int t = Targets(s, from, targets);
                        for (int i = 0; i < t; i++) into[k++] = new GameAction(ActionKind.Chain, targets[i]);
                    }
                    into[k++] = new GameAction(ActionKind.StopChain);
                    break;
                case PendingKind.Ratway:
                    for (int n = 1; n < GameData.Cells; n++) into[k++] = new GameAction(ActionKind.Ratway, (byte)n);
                    break;
            }
            return k;
        }
        if (s.Phase == Phase.Move)
        {
            for (int i = 0; i < s.DestinationCount; i++) into[k++] = new GameAction(ActionKind.Move, s.Destinations[i]);
            return k;
        }
        // Make Your Decision: a Consume or a Hunt, and a Replace before it, each once a day.
        if (s.Cells[s.Position].HasInmate)
        {
            Span<byte> targets = stackalloc byte[GameData.Cells];
            if (s.Used == DayAction.None)
            {
                int t = Targets(s, s.Position, targets);
                for (int i = 0; i < t; i++) into[k++] = new GameAction(ActionKind.Consume, targets[i]);
            }
            if (!s.Replaced && s.Used == DayAction.None) into[k++] = new GameAction(ActionKind.Replace);
            int h = HuntTargets(s, targets);
            for (int i = 0; i < h; i++) into[k++] = new GameAction(ActionKind.Hunt, targets[i]);
        }
        into[k++] = new GameAction(ActionKind.End);
        return k;
    }

    public bool IsLegal(in GameState s, GameAction a)
    {
        Span<GameAction> legal = stackalloc GameAction[MaxActions];
        int k = Legal(s, legal);
        for (int i = 0; i < k; i++) if (legal[i] == a) return true;
        return false;
    }

    /// <summary>Plays one action. Returns false (and changes nothing) for an action that isn't legal now.</summary>
    public bool Apply(ref GameState s, GameAction a)
    {
        if (!IsLegal(s, a)) return false;
        switch (a.Kind)
        {
            case ActionKind.PickInstrument: TakeInstrument(ref s, (Instrument)a.Arg); break;
            case ActionKind.Reroll: Reroll(ref s); break;
            case ActionKind.PickTreasure: TakeTreasure(ref s, (Treasure)a.Arg); break;
            case ActionKind.Interrogate:
                PopPending(ref s);
                if (a.Arg == 0) Instigate(ref s); else Bestow(ref s);
                break;
            case ActionKind.Chain: ChainBite(ref s, a.Arg); break;
            case ActionKind.StopChain: PopPending(ref s); break;
            case ActionKind.Ratway:
                PopPending(ref s);
                Land(ref s, a.Arg);
                break;
            case ActionKind.Move:
                s.Phase = Phase.Act;
                s.DestinationCount = 0;
                Land(ref s, a.Arg);
                break;
            case ActionKind.Consume: Consume(ref s, a.Arg); break;
            case ActionKind.Replace: Replace(ref s); break;
            case ActionKind.Hunt: Hunt(ref s, a.Arg); break;
            case ActionKind.End: EndDay(ref s); break;
        }
        // A day's action done (a Consume's chain over): now and then a new marker.
        if (a.Kind is ActionKind.Consume or ActionKind.Chain or ActionKind.StopChain or ActionKind.Replace or ActionKind.Hunt
            && !HasPending(s, PendingKind.Chain) && s.Rng.Chance(Options.IncidentActionPercent))
            AddMarkers(ref s, 1, unopenedOnly: false, avoidWarden: true);
        Settle(ref s);
        return true;
    }

    private static bool HasPending(in GameState s, PendingKind kind)
    {
        for (int i = 0; i < s.PendingCount; i++) if (s.Pending[i].Kind == kind) return true;
        return false;
    }

    /// <summary>After an action: chain choices re-read against the new Sandbox, markers that no longer fit gone, a forced End taken.</summary>
    private void Settle(ref GameState s)
    {
        while (true)
        {
            RefreshChains(ref s);
            DropUnfitMarkers(ref s);
            KeepMinMarkers(ref s);
            if (!AutoEnd || s.Phase != Phase.Act || s.PendingCount > 0) return;
            // Only End left (the game then ends the day by itself).
            Span<GameAction> legal = stackalloc GameAction[MaxActions];
            if (Legal(s, legal) == 1)
            {
                EndDay(ref s);
                continue;
            }
            return;
        }
    }

    // ---------- The day ----------

    private void BeginDay(ref GameState s)
    {
        s.Phase = Phase.Move;
        s.Used = DayAction.None;
        s.Replaced = false;
        CheckPrey(ref s);
        PreyRise(ref s);
        CheckPrey(ref s);
        HatchNests(ref s);
        MorningInstruments(ref s);
        if (s.Day > 1 && s.Rng.Chance(Options.IncidentDayPercent)) AddMarkers(ref s, 1, unopenedOnly: false, avoidWarden: true);
        RollDestinations(ref s);
    }

    private void EndDay(ref GameState s)
    {
        if (s.Day >= s.MaxDays)
        {
            s.Phase = Phase.Ended;
            s.DestinationCount = 0;
            s.PendingCount = 0;
            return;
        }
        s.Day++;
        BeginDay(ref s);
    }

    /// <summary>"-1 daily destination option" instruments held.</summary>
    private static int FewerDestinations(in GameState s)
    {
        int n = 0;
        for (int i = 0; i < GameData.InstrumentCount; i++)
            if (s.Has((Instrument)i)) n += GameData.Info((Instrument)i).FewerDestinations;
        return n;
    }

    /// <summary>
    /// Today's Destinations: cells 1-6 steps clockwise ahead, drawn one at a time without repeats, the Interrogation
    /// Room at <see cref="RuleOptions.RoomOdds"/> of another cell's odds; nearest first.
    /// </summary>
    private void RollDestinations(ref GameState s)
    {
        int fewer = FewerDestinations(s) + (s.Rng.Chance(Options.FewerDestinationsPercent) ? 1 : 0);
        int count = Math.Clamp(GameData.Destinations - fewer, 1, GameData.Destinations);
        Span<double> w = stackalloc double[GameData.Reach];
        for (int i = 0; i < GameData.Reach; i++)
            w[i] = (s.Position + i + 1) % GameData.Cells == GameData.Room ? Options.RoomOdds : 1;
        Span<byte> steps = stackalloc byte[GameData.Destinations];
        int k = 0;
        while (k < count)
        {
            int i = s.Rng.Pick(w);
            if (i < 0) break;
            w[i] = 0;
            steps[k++] = (byte)(i + 1);
        }
        steps[..k].Sort();
        for (int i = 0; i < k; i++) s.Destinations[i] = (byte)((s.Position + steps[i]) % GameData.Cells);
        s.DestinationCount = (byte)k;
    }

    public static int Steps(int from, int to) => (to - from + GameData.Cells) % GameData.Cells;

    /// <summary>The Warden moves onto cell <paramref name="n"/> (a Destination or the Sewer Ratway) and the landing plays out.</summary>
    private void Land(ref GameState s, int n)
    {
        var marker = n == GameData.Room ? Incident.None : s.Cells[n].Marker;
        s.Cells[n].Marker = Incident.None;
        s.Cells[n].MarkerKnown = false;
        int steps = Steps(s.Position, n);
        if (s.ChainReaction)
        {
            // Chain Reaction: every Unopened cell on the way opens, the last one too (no other effects).
            s.ChainReaction = false;
            if (steps == 0) OpenCell(ref s, n, How.Side);
            for (int k = 1; k <= steps; k++) OpenCell(ref s, (s.Position + k) % GameData.Cells, How.Side);
        }
        else if (n != GameData.Room && s.Cells[n].Sealed) OpenCell(ref s, n, How.Patrol);
        s.Position = (byte)n;
        s.Stats.Moved += (short)steps;
        AdvanceObjective(ref s, ObjectiveKind.PrisonWalk, steps);
        Shackles(ref s, steps);
        if (n == GameData.Room) InterrogationRoom(ref s);
        CheckPrey(ref s);
        if (marker != Incident.None) FireIncident(ref s, marker, n);
        if (s.Rng.Chance(Options.IncidentMovePercent)) AddMarkers(ref s, 1, unopenedOnly: false, avoidWarden: true);
    }

    /// <summary>A stop in the Interrogation Room: Instigate or Bestow (Tyranny: both, 3 more times, first).</summary>
    private void InterrogationRoom(ref GameState s)
    {
        if (s.Theme == Theme.Tyranny)
            for (int i = 0; i < 3; i++)
            {
                Instigate(ref s);
                Bestow(ref s);
            }
        PushPending(ref s, new Pending { Kind = PendingKind.Interrogation });
    }

    /// <summary>Instigate: a Prison Incident marker on a random free cell (two on a Sandbox with none).</summary>
    private void Instigate(ref GameState s)
    {
        bool none = true;
        for (int n = 1; n < GameData.Cells; n++) if (s.Cells[n].Marker != Incident.None) none = false;
        AddMarkers(ref s, none ? Options.InstigateMarkersOnEmpty : Options.InstigateMarkers, unopenedOnly: false, avoidWarden: false);
    }

    /// <summary>Bestow: a random Inmate rises a tier.</summary>
    private void Bestow(ref GameState s)
    {
        Span<ushort> ids = stackalloc ushort[GameData.Cells];
        int k = BoardIds(s, ids);
        if (k > 0) Promote(ref s, ids[s.Rng.Below(k)], 1, reroll: false);
    }
}
