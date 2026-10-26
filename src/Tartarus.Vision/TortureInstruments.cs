namespace Tartarus.Vision;

/// <summary>A Torture Instrument: its id (as the slot network and labels write it), the game's name, and what it does.</summary>
public sealed record TortureInstrument(string Id, string Name, string Trigger, string Effect)
{
    /// <summary>The name in two lines for a caption: "Filthy Claws" over "Alpha Wolf".</summary>
    public string Caption => Name.Replace(": ", "\n", StringComparison.Ordinal);
}

/// <summary>
/// The 31 Torture Instruments of the season, in the game's data order (icon NN in the game's files is the NN-th),
/// with their icons in models/instruments/&lt;id&gt;.png.
/// </summary>
public static class TortureInstruments
{
    public static readonly IReadOnlyList<TortureInstrument> All =
    [
        new("muzzle", "Raging Beast's Agonizing Muzzle", "When there are no more than 4 Inmates on the field, after Consuming", "+2 additional Rarity Tiers to this Inmate"),
        new("chains", "Ferocious Beast's Ravenous Chains", "At the start of each day", "Removes Inmates on both sides of the Inmate with the highest rarity; for each Inmate removed, there is a 40% chance to grant that Inmate +1 Rarity Tier"),
        new("collar", "Maddened Beast's Barbed Collar", "When an Inmate Chain Consumes, there is an 80% chance to trigger", "+1 additional Rarity Tier to this Inmate"),
        new("casket", "Avenger's Casket", "There is a 30% chance to trigger after Consuming", "Randomly opens 1 cell. The Inmate inside is guaranteed to be Consumable for the current Inmate"),
        new("cage", "Crowded Beastcage", "There is a 40% chance to trigger after the Inmate is Consumed", "The cell occupied by this Inmate is immediately opened"),
        new("whip", "Barbed Whip", "-1 daily destination option; there is a 35% chance to trigger after Consuming", "+1 additional Rarity Tier to this Inmate"),
        new("saw", "Rusty Saw", "There is a 25% chance to trigger after Consuming", "+1 additional Rarity Tier to this Inmate"),
        new("blacklist", "Executioner's Blacklist", "When there are no more than 5 unopened cells, after opening", "+1 Rarity Tier to 2 random Inmates"),
        new("keys", "Lost Ring of Keys", "There is an 80% chance to trigger after opening", "Simultaneously opens cells on both sides"),
        new("coin", "Kin's Coin of Fate", "At the start of each day, if there are matching Inmates on the field, there is a 60% chance to trigger", "Randomly removes 1 matching Inmate, then grants +2 Rarity Tiers to 1 of the remaining matching Inmates"),
        new("brand", "Searing Branding Iron", "There is a 35% chance to trigger after opening", "Replicate the Inmate in the current cell into 1 random unopened cell"),
        new("dogtag", "Felon's Dog Tag", "There is a 25% chance to trigger after opening", "+1 Rarity Tier to the current Inmate"),
        new("forged", "Forged Key", "-1 daily destination option; there is a 50% chance to trigger after opening", "Randomly opens 1 additional cell"),
        new("hook", "Sorting Hook", "After opening", "Inmates with Normal rarity will not appear"),
        new("scapegoat", "Scapegoat's Guillotine", "There are at least 2 other Inmates on the field when replacing", "Randomly removes 2 other Inmates to guarantee that the Inmate's rarity is increased after replacing"),
        new("axe", "Gaoler's Execution Axe", "If rarity is increased after replacing", "Randomly opens 2 cells"),
        new("maiden", "Severed Iron Maiden", "The quantities of this Inmate's Higher-Tier and Lower-Tier Inmates are equal", "+3 additional Rarity Tiers to the Inmate after this Replacement"),
        new("scales", "Warden's Scales", "There is an 80% chance to trigger after replacing", "Reveals the Inmate with the highest rarity among unopened cells"),
        new("noose", "Hangman's Noose", "After replacing", "Inmates with Normal or Magic rarity will not appear"),
        new("stake", "Burning Stake", "-1 daily destination option; when an Inmate below Legendary is Replaced, there is a 40% chance to trigger", "Changes Inmate rarity to Legendary"),
        new("pestle", "Iron Pestle", "After replacing", "The Inmate retains their original Code"),
        new("shortchain", "Short-Chained Shackles", "There is a 50% chance to trigger when moving no more than 3 tiles in a single movement", "Randomly opens 1 cell"),
        new("shackles", "Long-Chained Shackles", "There is a 40% chance to trigger when moving 4 or more tiles in a single movement", "+1 Rarity Tier to 1 random Inmate"),
        new("tongs", "Iron Tongs of Lust: Mammoth", "At the start of each day", "Randomly adds a Tusk mark to 1 cell occupied by Mammoth"),
        new("maul", "Elephant-Foot Maul: Mammoth", "When Mammoth Consumes", "Mammoth can Consume higher-rarity countered Inmates; after Consuming, its rarity becomes +2 tiers above the consumed target's"),
        new("trap", "Blood-Stained Trap: Berserk Lion", "Stops at Berserk Lion's cell", "Adds Hunt to decision options, which can add a Prey mark to Consumable targets"),
        new("crown", "Iron Crown of the Prison King: Berserk Lion", "When Berserk Lion Consumes", "Berserk Lion can Consume any Inmate of the same rarity. +2 additional Rarity Tiers after consuming"),
        new("claws", "Filthy Claws: Alpha Wolf", "At the start of each day", "Randomly adds a Scratch Marks mark to 1 unopened cell"),
        new("guillotine", "Fanged Guillotine: Alpha Wolf", "When Alpha Wolf Consumes", "Alpha Wolf can Consume lower-rarity countered Inmates, triggering Oppress after Consuming"),
        new("ironbox", "Squirming Iron Box: Colossal Rat", "At the start of each day", "Randomly adds a Rat Nest mark to 1 unopened cell"),
        new("skewer", "Skewered Rat Heads: Colossal Rat", "When Colossal Rat Consumes", "Colossal Rat can Consume Colossal Rats of the same rarity. +2 additional Rarity Tiers after consuming"),
    ];

    private static readonly Dictionary<string, TortureInstrument> ById = All.ToDictionary(i => i.Id);

    /// <summary>
    /// Instruments whose marks the cell network hasn't seen enough of (Tusk, Prey, Rat Nest): rare in offers (weight 5
    /// against 40), so the pick screen points them out; holding one fills the dataset with its marks.
    /// </summary>
    public static readonly IReadOnlySet<string> WantedForData = new HashSet<string> { "tongs", "trap", "ironbox" };

    public static TortureInstrument? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>The game's name of an id; the id itself when unknown ("?" from the slot reader).</summary>
    public static string NameOf(string id) => Find(id)?.Name ?? id;

    /// <summary>
    /// The instrument whose name a read text is (OCR's slips allowed): the most alike name by edit distance over
    /// letters and digits only, and how alike, 0-1.
    /// </summary>
    public static (TortureInstrument Instrument, double Score) Match(string text) => NameMatch.ByName(text, All, i => i.Id, i => i.Name, "instruments");

    /// <summary>models/instruments (the icons), looked for up from the program's folder.</summary>
    public static string? FindIconDir() => NameMatch.FindModelDir("instruments", "coin.png");
}

/// <summary>A Warden's Treasure: the reward a Chromatic brings (pick one of four).</summary>
public sealed record WardenTreasure(string Id, string Name, string Effect)
{
    public string Caption => Name;
}

/// <summary>The four Warden's Treasures, in the game's data order; icons, once cut from pick screens, in models/treasures/&lt;id&gt;.png.</summary>
public static class WardenTreasures
{
    public static readonly IReadOnlyList<WardenTreasure> All =
    [
        new("ring", "Three-Headed Thumb Ring", "Randomly open up to 3 cells (does not trigger other effects)"),
        new("horn", "Brass Horn of Strife", "All Inmates with Consumable targets randomly Consume 1 Consumable target in turn"),
        new("vintage", "Crimson Vintage", "Increase the rarity of all Inmates below Legendary rarity by 1 tier"),
        new("sin", "Brand of Original Sin", "Randomly select up to 4 Inmates without Consumable targets, and adjust their Code so they can find Consumable targets"),
    ];

    private static readonly Dictionary<string, WardenTreasure> ById = All.ToDictionary(t => t.Id);

    public static WardenTreasure? Find(string id) => ById.GetValueOrDefault(id);

    public static string NameOf(string id) => Find(id)?.Name ?? id;

    public static (WardenTreasure Treasure, double Score) Match(string text) => NameMatch.ByName(text, All, t => t.Id, t => t.Name, "treasures");

    public static string? FindIconDir() => NameMatch.FindModelDir("treasures", null);

    /// <summary>
    /// The game's files' treasure pictures aren't at hand: the first pick screen that shows a treasure gives its
    /// icon, cut from its card into models/treasures (next to models/instruments). Returns the ids saved.
    /// </summary>
    public static List<string> SaveMissingIcons(Frame frame, ScreenLayout layout, PickRead pick)
    {
        var saved = new List<string>();
        if (pick.Kind != PickRead.Treasure) return saved;
        string? dir = FindIconDir() ?? (TortureInstruments.FindIconDir() is { } instruments ? Path.Combine(Path.GetDirectoryName(instruments)!, "treasures") : null);
        if (dir == null) return saved;
        foreach (var card in pick.Cards)
        {
            if (card.Id == null || File.Exists(Path.Combine(dir, card.Id + ".png"))) continue;
            Directory.CreateDirectory(dir);
            ImageIO.SavePng(layout.Crop(frame, ScreenLayout.TreasureIcon(card.Index)), Path.Combine(dir, card.Id + ".png"));
            saved.Add(card.Id);
        }
        return saved;
    }
}

/// <summary>Matching OCR'd names to known ones, and finding model folders.</summary>
public static class NameMatch
{
    /// <summary>The most alike name by edit distance over letters and digits only (OCR drops apostrophes, glues words), and how alike, 0-1.</summary>
    public static (T Item, double Score) Best<T>(string text, IReadOnlyList<T> items, Func<T, string> name)
    {
        string read = Letters(text);
        T best = items[0];
        double bestScore = -1;
        foreach (var item in items)
        {
            string n = Letters(name(item));
            double score = 1 - (double)EditDistance(read, n) / Math.Max(1, Math.Max(read.Length, n.Length));
            if (score > bestScore) (best, bestScore) = (item, score);
        }
        return (best, Math.Max(0, bestScore));
    }

    /// <summary>
    /// The item whose name, in English or any language of the game (<see cref="GameText"/>'s <paramref name="section"/>),
    /// is likest the read text, and how alike.
    /// </summary>
    public static (T Item, double Score) ByName<T>(string text, IReadOnlyList<T> items, Func<T, string> id, Func<T, string> name, string section)
    {
        var byId = items.ToDictionary(id);
        var names = items.Select(i => (Id: id(i), Name: name(i))).Concat(GameText.Names(section).Where(n => byId.ContainsKey(n.Id)).Select(n => (n.Id, Name: n.Text))).ToList();
        var (best, score) = Best(text, names, n => n.Name);
        return (byId[best.Id], score);
    }

    private static string Letters(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>models/&lt;name&gt; beside the program or in the repository it runs from (holding <paramref name="probe"/> if given).</summary>
    public static string? FindModelDir(string name, string? probe)
    {
        foreach (string dir in AppFiles.Folders)
        {
            string candidate = Path.Combine(dir, "models", name);
            if (probe != null ? File.Exists(Path.Combine(candidate, probe)) : Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static int EditDistance(string a, string b)
    {
        var row = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) row[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int up = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = up;
            }
        }
        return row[b.Length];
    }
}

/// <summary>A run theme: an effect for the whole run, shown before it starts (about 1 run in 8).</summary>
public sealed record RunTheme(string Id, string Name, string Effect);

/// <summary>The four run themes, in the game's data order.</summary>
public static class RunThemes
{
    public static readonly IReadOnlyList<RunTheme> All =
    [
        new("tyranny", "Tyranny", "Automatically triggers Instigate and Bestow 3 additional times each time you stop at the Interrogation Room"),
        new("opulence", "Opulence", "You can make 2 additional choices when obtaining Warden's Treasure"),
        new("ironlaw", "Iron Law", "Extends the duration of the current run to 30 days"),
        new("cagefight", "Cage Fight", "Inmates can Consume lower-rarity countered Inmates; Consuming does not randomly change their Codes"),
    ];

    private static readonly Dictionary<string, RunTheme> ById = All.ToDictionary(t => t.Id);

    public static RunTheme? Find(string id) => ById.GetValueOrDefault(id);

    public static string NameOf(string id) => Find(id)?.Name ?? id;

    public static (RunTheme Theme, double Score) Match(string text) => NameMatch.ByName(text, All, t => t.Id, t => t.Name, "themes");
}
