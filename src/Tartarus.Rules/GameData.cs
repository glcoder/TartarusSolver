namespace Tartarus.Rules;

// The game's own data (the season's client tables, as github.com/arcasti/tli-warden's data/warden-data.json has
// them: facts, not code) and, where the game's tables say nothing, what players' boards show over many runs (the
// tartarus-simulator's live statistics). Terms follow the game's rules screens.

/// <summary>An Inmate's Code. Who counters whom: Mammoth → Lion, Wolf; Lion → Wolf, Rat; Wolf → Rat; Rat → Mammoth.</summary>
public enum Code : byte { Mammoth, Lion, Wolf, Rat }

/// <summary>An Inmate's rarity. Chromatic leaves the Sandbox for the Chromatic panel at once.</summary>
public enum Rarity : byte { Normal, Magic, Rare, Legendary, Ultimate, Chromatic }

/// <summary>The 31 Torture Instruments, in the game's data order (their icons are numbered so).</summary>
public enum Instrument : byte
{
    Muzzle, Chains, Collar, Casket, Cage, Whip, Saw,
    Blacklist, Keys, Coin, Brand, Dogtag, Forged, Hook,
    Scapegoat, Axe, Maiden, Scales, Noose, Stake, Pestle,
    Shortchain, Shackles,
    Tongs, Maul, Trap, Crown, Claws, Guillotine, Ironbox, Skewer,
}

/// <summary>The Prison Incidents a marker ("!") can hold.</summary>
public enum Incident : byte
{
    None,
    HighRiskConvict, TasteOfFreedom, BehindClosedDoors,
    RavenousAppetite, ExtraPortion, SharedFortune,
    Archenemy, ExecutionEfficiency,
    CrowWhispers, TreasureHunt,
    SewerRatway, ChainReaction,
    TransferOfConvicts,
    Usurpation1, Usurpation2, Usurpation3, Usurpation4,
}

/// <summary>The Warden's Treasures a Chromatic brings (pick one of four).</summary>
public enum Treasure : byte { ThumbRing, BrassHorn, CrimsonVintage, BrandOfSin }

/// <summary>A run's theme, shown before the run (about 1 run in 8).</summary>
public enum Theme : byte { None, Tyranny, Opulence, IronLaw, CageFight }

/// <summary>The run's Objective; completing it brings a second Torture Instrument pick.</summary>
public enum ObjectiveKind : byte
{
    /// <summary>King of the Caged: at least 1 Inmate's rarity reaches Ultimate.</summary>
    KingOfTheCaged,
    /// <summary>Survival of the Fittest: Consume 4 times.</summary>
    SurvivalOfTheFittest,
    /// <summary>Behind Cell Doors: open 10 cells.</summary>
    BehindCellDoors,
    /// <summary>Undercurrents: trigger a Prison Incident 2 times.</summary>
    Undercurrents,
    /// <summary>Prison Walk: move 18 tiles.</summary>
    PrisonWalk,
}

/// <summary>Where a Prison Incident's marker can sit.</summary>
public enum Spawn : byte { Unopened, Opened, Any }

/// <summary>A Torture Instrument's group in the game's tables; the species ones are rare in offers and exclude each other.</summary>
public enum InstrumentGroup : byte { Consume, Open, Replace, Move, Species }

public sealed record InstrumentInfo(Instrument Instrument, string Id, string Name, InstrumentGroup Group, int Weight, Code? Species, int FewerDestinations);

public sealed record IncidentInfo(Incident Incident, string Name, int Weight, Spawn Spawn);

public static class GameData
{
    public const int Cells = 16;
    /// <summary>Cell 1 on the screen, index 0 here: the Interrogation Room, which never holds an Inmate.</summary>
    public const int Room = 0;
    public const int Days = 20;
    public const int IronLawDays = 30;
    public const int ChromaticCap = 10;
    public const int Destinations = 3;
    /// <summary>Destinations come from this many cells ahead, clockwise.</summary>
    public const int Reach = 6;
    public const int InstrumentOffer = 3;

    /// <summary>Who each Code counters (may consume at the same rarity).</summary>
    public static bool Counters(Code eater, Code meal) => eater switch
    {
        Code.Mammoth => meal is Code.Lion or Code.Wolf,
        Code.Lion => meal is Code.Wolf or Code.Rat,
        Code.Wolf => meal == Code.Rat,
        Code.Rat => meal == Code.Mammoth,
        _ => false,
    };

    /// <summary>The Codes each Code counters.</summary>
    public static ReadOnlySpan<Code> Prey(Code eater) => eater switch
    {
        Code.Mammoth => [Code.Lion, Code.Wolf],
        Code.Lion => [Code.Wolf, Code.Rat],
        Code.Wolf => [Code.Rat],
        _ => [Code.Mammoth],
    };

    public static readonly IReadOnlyList<InstrumentInfo> Instruments =
    [
        new(Instrument.Muzzle, "muzzle", "Raging Beast's Agonizing Muzzle", InstrumentGroup.Consume, 40, null, 0),
        new(Instrument.Chains, "chains", "Ferocious Beast's Ravenous Chains", InstrumentGroup.Consume, 40, null, 0),
        new(Instrument.Collar, "collar", "Maddened Beast's Barbed Collar", InstrumentGroup.Consume, 40, null, 0),
        new(Instrument.Casket, "casket", "Avenger's Casket", InstrumentGroup.Consume, 40, null, 0),
        new(Instrument.Cage, "cage", "Crowded Beastcage", InstrumentGroup.Consume, 40, null, 0),
        new(Instrument.Whip, "whip", "Barbed Whip", InstrumentGroup.Consume, 40, null, 1),
        new(Instrument.Saw, "saw", "Rusty Saw", InstrumentGroup.Consume, 40, null, 0),
        new(Instrument.Blacklist, "blacklist", "Executioner's Blacklist", InstrumentGroup.Open, 40, null, 0),
        new(Instrument.Keys, "keys", "Lost Ring of Keys", InstrumentGroup.Open, 40, null, 0),
        new(Instrument.Coin, "coin", "Kin's Coin of Fate", InstrumentGroup.Open, 40, null, 0),
        new(Instrument.Brand, "brand", "Searing Branding Iron", InstrumentGroup.Open, 40, null, 0),
        new(Instrument.Dogtag, "dogtag", "Felon's Dog Tag", InstrumentGroup.Open, 40, null, 0),
        new(Instrument.Forged, "forged", "Forged Key", InstrumentGroup.Open, 40, null, 1),
        new(Instrument.Hook, "hook", "Sorting Hook", InstrumentGroup.Open, 40, null, 0),
        new(Instrument.Scapegoat, "scapegoat", "Scapegoat's Guillotine", InstrumentGroup.Replace, 40, null, 0),
        new(Instrument.Axe, "axe", "Gaoler's Execution Axe", InstrumentGroup.Replace, 40, null, 0),
        new(Instrument.Maiden, "maiden", "Severed Iron Maiden", InstrumentGroup.Replace, 40, null, 0),
        new(Instrument.Scales, "scales", "Warden's Scales", InstrumentGroup.Replace, 40, null, 0),
        new(Instrument.Noose, "noose", "Hangman's Noose", InstrumentGroup.Replace, 40, null, 0),
        new(Instrument.Stake, "stake", "Burning Stake", InstrumentGroup.Replace, 40, null, 1),
        new(Instrument.Pestle, "pestle", "Iron Pestle", InstrumentGroup.Replace, 40, null, 0),
        new(Instrument.Shortchain, "shortchain", "Short-Chained Shackles", InstrumentGroup.Move, 40, null, 0),
        new(Instrument.Shackles, "shackles", "Long-Chained Shackles", InstrumentGroup.Move, 40, null, 0),
        new(Instrument.Tongs, "tongs", "Iron Tongs of Lust: Mammoth", InstrumentGroup.Species, 5, Code.Mammoth, 0),
        new(Instrument.Maul, "maul", "Elephant-Foot Maul: Mammoth", InstrumentGroup.Species, 5, Code.Mammoth, 0),
        new(Instrument.Trap, "trap", "Blood-Stained Trap: Berserk Lion", InstrumentGroup.Species, 5, Code.Lion, 0),
        new(Instrument.Crown, "crown", "Iron Crown of the Prison King: Berserk Lion", InstrumentGroup.Species, 5, Code.Lion, 0),
        new(Instrument.Claws, "claws", "Filthy Claws: Alpha Wolf", InstrumentGroup.Species, 5, Code.Wolf, 0),
        new(Instrument.Guillotine, "guillotine", "Fanged Guillotine: Alpha Wolf", InstrumentGroup.Species, 5, Code.Wolf, 0),
        new(Instrument.Ironbox, "ironbox", "Squirming Iron Box: Colossal Rat", InstrumentGroup.Species, 5, Code.Rat, 0),
        new(Instrument.Skewer, "skewer", "Skewered Rat Heads: Colossal Rat", InstrumentGroup.Species, 5, Code.Rat, 0),
    ];

    public const int InstrumentCount = 31;

    public static InstrumentInfo Info(Instrument i) => Instruments[(int)i];

    /// <summary>An instrument by the id the slot network and labels use ("muzzle", "coin", ...).</summary>
    public static Instrument? InstrumentById(string id)
    {
        foreach (var info in Instruments)
            if (info.Id == id) return info.Instrument;
        return null;
    }

    /// <summary>
    /// Whether holding <paramref name="held"/> keeps <paramref name="other"/> out of offers: a species instrument
    /// keeps out those of the other species (the game's exclude lists).
    /// </summary>
    public static bool Excludes(Instrument held, Instrument other)
    {
        var (a, b) = (Info(held), Info(other));
        return a.Species is { } s && b.Species is { } t && s != t;
    }

    public static readonly IReadOnlyList<IncidentInfo> Incidents =
    [
        new(Incident.None, "", 0, Spawn.Any),
        new(Incident.HighRiskConvict, "High-Risk Convict", 40, Spawn.Unopened),
        new(Incident.TasteOfFreedom, "Taste of Freedom", 40, Spawn.Unopened),
        new(Incident.BehindClosedDoors, "Behind Closed Doors", 40, Spawn.Unopened),
        new(Incident.RavenousAppetite, "Ravenous Appetite", 30, Spawn.Opened),
        new(Incident.ExtraPortion, "Extra Portion", 30, Spawn.Opened),
        new(Incident.SharedFortune, "Shared Fortune", 20, Spawn.Opened),
        new(Incident.Archenemy, "Archenemy", 30, Spawn.Opened),
        new(Incident.ExecutionEfficiency, "Execution Efficiency", 20, Spawn.Opened),
        new(Incident.CrowWhispers, "Crow Whispers", 10, Spawn.Any),
        new(Incident.TreasureHunt, "\"Treasure\" Hunt", 10, Spawn.Any),
        new(Incident.SewerRatway, "Sewer Ratway", 20, Spawn.Any),
        new(Incident.ChainReaction, "Chain Reaction", 20, Spawn.Any),
        new(Incident.TransferOfConvicts, "Transfer of Convicts", 20, Spawn.Opened),
        // Usurpation II-IV join the Prison Incidents one after another, each taking the last one's place (weight).
        new(Incident.Usurpation1, "Usurpation I", 10, Spawn.Any),
        new(Incident.Usurpation2, "Usurpation II", 0, Spawn.Any),
        new(Incident.Usurpation3, "Usurpation III", 0, Spawn.Any),
        new(Incident.Usurpation4, "Usurpation IV", 0, Spawn.Any),
    ];

    public const int IncidentCount = 18;

    public static IncidentInfo Info(Incident i) => Incidents[(int)i];

    public static string Name(Treasure t) => t switch
    {
        Treasure.ThumbRing => "Three-Headed Thumb Ring",
        Treasure.BrassHorn => "Brass Horn of Strife",
        Treasure.CrimsonVintage => "Crimson Vintage",
        _ => "Brand of Original Sin",
    };

    public static string Name(Theme t) => t switch
    {
        Theme.Tyranny => "Tyranny",
        Theme.Opulence => "Opulence",
        Theme.IronLaw => "Iron Law",
        Theme.CageFight => "Cage Fight",
        _ => "",
    };

    /// <summary>What an Objective asks for: the count to reach.</summary>
    public static int Target(ObjectiveKind k) => k switch
    {
        ObjectiveKind.KingOfTheCaged => 1,
        ObjectiveKind.SurvivalOfTheFittest => 4,
        ObjectiveKind.BehindCellDoors => 10,
        ObjectiveKind.Undercurrents => 2,
        _ => 18,
    };

    public static string Name(ObjectiveKind k) => k switch
    {
        ObjectiveKind.KingOfTheCaged => "King of the Caged",
        ObjectiveKind.SurvivalOfTheFittest => "Survival of the Fittest",
        ObjectiveKind.BehindCellDoors => "Behind Cell Doors",
        ObjectiveKind.Undercurrents => "Undercurrents",
        _ => "Prison Walk",
    };
}
