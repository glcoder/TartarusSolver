namespace Tartarus.Vision;

/// <summary>
/// Which readings of the Sandbox a move hint may be made of. A new position counts once two scans in a row read it
/// alike: a frame in the middle of an animation (a Chromatic's flight, its Ultimate read Normal as it goes, a popup
/// fading in) reads otherwise on the next one. And after the game's log tells of a change on the Sandbox (a bite, a
/// Chromatic, a Prison Incident), a reading like those just before it is the old Sandbox still on screen while its
/// animation plays, for a few seconds at most (2026-10-27: an eaten Inmate stood in its cell for up to 5 s, and the
/// hint framed it as the chain's next bite).
/// </summary>
public sealed class BoardSettle
{
    public enum Verdict { Take, Unsettled, Stale }

    /// <summary>How far before the log's change a reading counts as the old Sandbox.</summary>
    public static readonly TimeSpan Before = TimeSpan.FromSeconds(3);
    /// <summary>How long after the log's change the old Sandbox may still be on screen.</summary>
    public static readonly TimeSpan StaleFor = TimeSpan.FromSeconds(4);

    private readonly List<(DateTime At, string Key)> _reads = [];
    private string? _last;

    /// <summary>
    /// What a reading of the Sandbox says as far as a hint goes: each cell (not its Consumable mark, which pulses and
    /// the rules tell anyway, nor whether a tooltip covers it), the Warden, the phase and the Destinations.
    /// </summary>
    public static string Key(EditableSandbox b) =>
        string.Join(",", b.Cells.Select(c => $"{c.Kind}:{c.Code}:{c.Rarity}:{c.VisibleRarity}:{(c.Nest ? "n" : "")}{(c.Incident ? "i" : "")}{(c.ScratchMarks ? "s" : "")}{(c.Tusk ? "t" : "")}{(c.Prey ? "p" : "")}"))
        + $"|{b.Warden}|{b.Phase}|{string.Join(" ", b.Destinations)}";

    /// <summary>
    /// How long after a Warden's Treasure is taken the old Sandbox may still be on screen: its window closes ~0.4 s
    /// after the Confirm, and a Treasure that changes nothing leaves the old Sandbox for good (2026-11-01: no hint for 4 s
    /// after one).
    /// </summary>
    public static readonly TimeSpan StaleForTreasure = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A whole Sandbox read on a frame captured at <paramref name="at"/>; <paramref name="changedAt"/>: the log's last
    /// change on the Sandbox, the old one counted as on screen for <paramref name="staleFor"/> after it (<see cref="StaleFor"/>).
    /// </summary>
    public Verdict Take(EditableSandbox b, DateTime at, DateTime changedAt, TimeSpan? staleFor = null)
    {
        string key = Key(b);
        bool stale = at > changedAt && at - changedAt < (staleFor ?? StaleFor)
            && _reads.Any(r => r.At <= changedAt && changedAt - r.At <= Before && r.Key == key);
        bool settled = key == _last;
        _last = key;
        _reads.Add((at, key));
        _reads.RemoveAll(r => at - r.At > Before + StaleFor);
        return stale ? Verdict.Stale : settled ? Verdict.Take : Verdict.Unsettled;
    }
}
