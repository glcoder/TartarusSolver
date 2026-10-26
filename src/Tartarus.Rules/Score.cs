namespace Tartarus.Rules;

/// <summary>
/// A run's result: the Chromatic Inmates contained (at most 10), and the Inmates left on the Sandbox by rarity, each
/// of which brings a Warden's Cache of its rarity at the Riot. Only opened cells count: an Inmate behind a door isn't
/// the Warden's.
/// </summary>
public readonly record struct RunScore(int Chromatic, int Ultimate, int Legendary, int Rare, int Magic, int Normal)
{
    public static RunScore Of(in GameState s)
    {
        Span<int> by = stackalloc int[6];
        for (int n = 1; n < GameData.Cells; n++)
            if (s.Cells[n].HasInmate) by[(int)s.Cells[n].Inmate.Rarity]++;
        return new RunScore(s.Chromatic, by[4], by[3], by[2], by[1], by[0]);
    }

    // Before Worth: static fields are set in order.
    public static readonly double[] DefaultWorth = [0, 0.002, 0.01, 0.05, 0.2];

    /// <summary>
    /// What a Warden's Cache for an Inmate left on the Sandbox is worth at the Riot, in Chromatics, by rarity (Normal,
    /// Magic, Rare, Legendary, Ultimate): the higher the rarity the richer the cache. Not a strict order of rarities: a
    /// sure Ultimate isn't thrown away for a 5% chance at a Chromatic. Set from the app's settings.
    /// </summary>
    public static IReadOnlyList<double> Worth { get; private set; } = DefaultWorth;

    /// <summary>Sets <see cref="Worth"/> (5 values, Normal to Ultimate, in Chromatics); before any search starts.</summary>
    public static void SetWorth(IReadOnlyList<double>? worth)
    {
        if (worth is { Count: 5 } && worth.All(w => w >= 0 && w <= 1)) Worth = worth.ToArray();
    }

    /// <summary>The run's worth in Chromatics: each Chromatic contained, plus each Inmate's cache (<see cref="Worth"/>).</summary>
    public double Utility => Chromatic + Normal * Worth[0] + Magic * Worth[1] + Rare * Worth[2] + Legendary * Worth[3] + Ultimate * Worth[4];

    public override string ToString() => $"Chromatic {Chromatic}, Ultimate {Ultimate}, Legendary {Legendary}, Rare {Rare}, Magic {Magic}, Normal {Normal}";
}
