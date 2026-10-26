namespace Tartarus.Rules;

/// <summary>
/// The numbers of the rules that the game's tables don't give, and the few readings of a card's text that could go
/// either way. Defaults: the game's tables where they speak, else what players' boards show over many runs
/// (tartarus-simulator's live statistics, 2026-10-16..22). One instance is shared by every state of a search.
/// </summary>
public sealed record RuleOptions
{
    public static readonly RuleOptions Default = new();

    /// <summary>Rarity weights (Normal .. Ultimate) of whoever is behind a door (the game's table).</summary>
    public double[] DoorWeights { get; init; } = [45, 35, 10, 8, 2];
    /// <summary>Rarity weights of the Inmate a Replace brings in (30,000 runs' boards).</summary>
    public double[] ReplaceWeights { get; init; } = [42, 34, 11.5, 9.5, 2.5];
    /// <summary>A Replace never brings in the Code it replaced (0.3% of 30,000 runs' Replaces did: read as never).</summary>
    public bool ReplaceNewCode { get; init; } = true;
    /// <summary>Rarity weights of the Inmates already out when a run starts (10,950 first boards).</summary>
    public double[] StartWeights { get; init; } = [44.5, 35, 10.5, 8, 2];
    /// <summary>Inmates out when a run starts; one fewer in <see cref="StartFewerPercent"/> % of runs.</summary>
    public int StartInmates { get; init; } = 4;
    public double StartFewerPercent { get; init; } = 5;

    /// <summary>
    /// The Interrogation Room among the morning's Destinations, when in reach, as a share of another cell's odds. The
    /// user's frames: among 3 cards on 33 of 64 such mornings (51.6%, as even odds give: 3 of 6); the dice land on
    /// it like on any cell (tli-warden's notes). tartarus-simulator's boards had said about 24% (0.37).
    /// </summary>
    public double RoomOdds { get; init; } = 1;

    /// <summary>
    /// % of mornings with one Destination card fewer (2 instead of 3) for no instrument's sake: 36 of 207 such mornings
    /// in the user's frames (17.4%).
    /// </summary>
    public double FewerDestinationsPercent { get; init; } = 17;

    /// <summary>% of runs that start with a Prison Incident marker on a random Unopened cell.</summary>
    public double IncidentStartPercent { get; init; } = 100;
    /// <summary>
    /// The Sandbox keeps at least this many markers (the game table's minIncidentMarkers): a fired or dropped marker is
    /// replaced at once. The user's frames: a marker on 95.5% of 1020 boards, on every run's first.
    /// </summary>
    public int MinIncidentMarkers { get; init; } = 1;
    /// <summary>
    /// % chances of a marker more on top of the minimum: at a day's start (from day 2), after a move, after a day's
    /// action. At 5 / 9 / 4, 64% of mornings would have 2 or more markers against 13.6% of the user's frames (9.2%
    /// without them): off by default.
    /// </summary>
    public double IncidentDayPercent { get; init; }
    public double IncidentMovePercent { get; init; }
    public double IncidentActionPercent { get; init; }
    /// <summary>% chance a fired Prison Incident is followed by a new marker on a random cell (never two).</summary>
    public double IncidentFollowPercent { get; init; } = 88;
    /// <summary>Instigate places 1 marker, or 2 on a Sandbox with none.</summary>
    public int InstigateMarkers { get; init; } = 1;
    public int InstigateMarkersOnEmpty { get; init; } = 2;

    /// <summary>Instrument Re-rolls the run has (the Pactspirits' "Remaining attempts", shared by every pick).</summary>
    public int Rerolls { get; init; } = 1;

    /// <summary>
    /// Severed Iron Maiden: the condition is read on the Inmate replaced (as many Inmates above its rarity as below
    /// it, before anything leaves); 20 of 20 recorded Replaces fit it. False: read on the new Inmate.
    /// </summary>
    public bool MaidenOnReplaced { get; init; } = true;
    /// <summary>Raging Beast's Agonizing Muzzle counts the Inmates on the field before the meal leaves it.</summary>
    public bool MuzzleCountsMeal { get; init; } = true;

    /// <summary>Tusk: Replacing an Inmate with the mark raises this many random Inmates a tier (the new one may be one).</summary>
    public int TuskInmates { get; init; } = 4;
    /// <summary>A Prey mark lasts this many mornings.</summary>
    public int PreyRounds { get; init; } = 3;
    /// <summary>A Rat Nest opens itself this many days after it appears, with a Rat of <see cref="NestRarity"/>.</summary>
    public int NestDays { get; init; } = 2;
    public Rarity NestRarity { get; init; } = Rarity.Rare;

    /// <summary>The run's objective is drawn at the start; null draws one of the five at equal odds.</summary>
    public ObjectiveKind? Objective { get; init; }
    /// <summary>The run's theme; null draws one (12% of runs, the game's table).</summary>
    public Theme? Theme { get; init; }
    public double ThemePercent { get; init; } = 12;
}
