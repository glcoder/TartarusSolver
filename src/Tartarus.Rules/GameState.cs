using System.Runtime.CompilerServices;

namespace Tartarus.Rules;

/// <summary>An Inmate. <see cref="Id"/> follows it from cell to cell (Prey marks, a Consume chain); 0 is nobody.</summary>
public struct Inmate
{
    public ushort Id;
    public Code Code;
    public Rarity Rarity;

    public readonly bool IsNone => Id == 0;
    public override readonly string ToString() => IsNone ? "-" : $"{Code} {Rarity}";
}

[Flags]
public enum CellMarks : byte
{
    None = 0,
    /// <summary>An Unopened cell shows its Inmate's rarity ("?" in that rarity's frame): Scratch Marks, Crow Whispers, the Scales.</summary>
    RarityShown = 1,
    /// <summary>Scratch Marks (Filthy Claws): a Wolf of the rarity inside can Consume through the door, +2 more tiers.</summary>
    Scratch = 2,
    /// <summary>Tusk (Iron Tongs of Lust): Replacing the Inmate here raises 4 random Inmates a tier.</summary>
    Tusk = 4,
}

/// <summary>
/// A cell of the Sandbox. Unopened (<see cref="Sealed"/>): <see cref="Inmate"/> is who is behind the door once
/// anyone has looked (a rarity shown, a Consume through Scratch Marks), else nobody yet: the door's Inmate is drawn
/// when it opens. Opened: <see cref="Inmate"/> is the Inmate in it. The Interrogation Room is neither and never
/// holds anyone.
/// </summary>
public struct Cell
{
    public bool Sealed;
    public Inmate Inmate;
    public CellMarks Marks;
    /// <summary>The day a Rat Nest here opens the cell; 0: no nest.</summary>
    public byte NestDue;
    /// <summary>A Prison Incident marker ("!") and the incident it holds.</summary>
    public Incident Marker;
    /// <summary>Whether the player knows the marker's incident (the game never shows it; a search re-draws unknown ones).</summary>
    public bool MarkerKnown;

    public readonly bool HasInmate => !Sealed && !Inmate.IsNone;
    public readonly bool Has(CellMarks m) => (Marks & m) != 0;
}

/// <summary>What the player is asked now, besides the day's move and decision.</summary>
public enum PendingKind : byte
{
    None,
    /// <summary>"Select a Torture Instrument": pick one of the offer (or Re-roll).</summary>
    Instrument,
    /// <summary>Warden's Treasure: pick one of the rewards left (Opulence: more than one pick).</summary>
    Treasure,
    /// <summary>Interrogation Room: Instigate or Bestow.</summary>
    Interrogation,
    /// <summary>Chain Consume: the eater may consume another of its targets, or stop.</summary>
    Chain,
    /// <summary>Sewer Ratway: move to any cell but the Interrogation Room.</summary>
    Ratway,
}

public struct Pending
{
    public PendingKind Kind;
    /// <summary>Instrument offer: up to 3 instruments. Treasure: <see cref="A"/> is a bit per reward left.</summary>
    public byte A, B, C;
    public byte Count;
    /// <summary>Treasure: picks left.</summary>
    public byte Picks;
    /// <summary>Chain: the eater's id.</summary>
    public ushort Eater;
    /// <summary>Instrument: the Objective's pick (else the run's first).</summary>
    public bool ForObjective;

    public readonly Instrument Offer(int i) => (Instrument)(i switch { 0 => A, 1 => B, _ => C });
}

/// <summary>A Prey mark (Blood-Stained Trap's Hunt): each morning the Lion and its prey rise a tier.</summary>
public struct PreyLink
{
    public ushort Lion, Prey;
    public Code PreyCode;
    public byte Rounds;
    public byte NextDay;
}

public enum Phase : byte
{
    /// <summary>Select a Destination.</summary>
    Move,
    /// <summary>Make Your Decision: Consume, Replace, Hunt or End (in the Interrogation Room: End after the card).</summary>
    Act,
    /// <summary>The last day is over: the Riot.</summary>
    Ended,
}

/// <summary>
/// The day's Consume (with its chain) or Hunt. A Replace is apart: once a day, and only before it (the game's log,
/// 2026-10-26: a Replace then a Consume on the same day, and days ending by themselves right after a Consume, never a
/// Replace after one; mid-chain Make Your Decision shows Consume, to go on, and End). End comes by itself when nothing
/// else is left.
/// </summary>
public enum DayAction : byte { None, Consume, Hunt }

[InlineArray(GameData.Cells)]
public struct CellArray { private Cell _e; }

[InlineArray(GameData.Destinations)]
public struct DestinationArray { private byte _e; }

[InlineArray(MaxPending)]
public struct PendingArray
{
    public const int MaxPending = 8;
    private Pending _e;
}

[InlineArray(MaxPrey)]
public struct PreyArray
{
    public const int MaxPrey = 6;
    private PreyLink _e;
}

/// <summary>
/// The whole game at one moment. A value type: copying it copies the game, so a search tries moves on copies.
/// Change it only through <see cref="Engine"/>.
/// </summary>
public struct GameState
{
    public CellArray Cells;
    public Rng Rng;
    /// <summary>1 .. <see cref="MaxDays"/>; Remaining Days = MaxDays - Day.</summary>
    public byte Day;
    public byte MaxDays;
    /// <summary>The Warden's cell (0 the Interrogation Room .. 15).</summary>
    public byte Position;
    public Phase Phase;
    public DayAction Used;
    /// <summary>Today's Replace is done (one a day).</summary>
    public bool Replaced;
    /// <summary>Today's Destinations, nearest first.</summary>
    public DestinationArray Destinations;
    public byte DestinationCount;
    /// <summary>Chain Reaction: the next move opens every Unopened cell it walks past.</summary>
    public bool ChainReaction;
    /// <summary>A bit per <see cref="Instrument"/> held.</summary>
    public uint Instruments;
    public byte Rerolls;
    /// <summary>Chromatic Inmates contained (at most <see cref="GameData.ChromaticCap"/>).</summary>
    public byte Chromatic;
    public Theme Theme;
    public ObjectiveKind Objective;
    public byte ObjectiveProgress;
    public bool ObjectiveDone;
    /// <summary>Which Usurpation is in the Prison Incidents now: 0 = I .. 3 = IV, 4 = none left.</summary>
    public byte UsurpationStage;
    public ushort NextId;
    public PendingArray Pending;
    public byte PendingCount;
    public PreyArray Prey;
    public byte PreyCount;
    public RunStats Stats;

    public readonly bool Has(Instrument i) => (Instruments & (1u << (int)i)) != 0;
    public readonly int RemainingDays => MaxDays - Day;
    public readonly bool Ended => Phase == Phase.Ended;
    /// <summary>The highest rarity an Inmate can reach now: Chromatic while the panel has room, else Ultimate.</summary>
    public readonly Rarity TopRarity => Chromatic < GameData.ChromaticCap ? Rarity.Chromatic : Rarity.Ultimate;
}

/// <summary>Counts over a run, for Objectives and reports.</summary>
public struct RunStats
{
    public byte Opened, Consumes, Incidents, Replaces, Hunts;
    public short Moved;
}
