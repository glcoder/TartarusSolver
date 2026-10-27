namespace Tartarus.Vision;

/// <summary>
/// On the Consume target pick (the red screen) nothing on the Sandbox changes until a bite, while the red flickers
/// over it (the glows, the target's jaws, the small mouth over each eater). Each Inmate there shows what its readings
/// over the pick's frames say most often, so one frame's misread doesn't flip it. The votes start over when the pick
/// ends or the cells' kinds change (a bite empties its meal's cell, Avenger's Casket opens one). No Inmate reads lower
/// than it was read just before the red screen: an Inmate's rarity never drops in its cell, while the red takes an
/// Ultimate's red frame away (a Mammoth Ultimate read Normal, and taken for a target).
/// </summary>
public sealed class PickVotes
{
    private readonly Dictionary<(Code? Code, Rarity Rarity), int>[] _votes = Enumerable.Range(0, 16).Select(_ => new Dictionary<(Code?, Rarity), int>()).ToArray();
    private readonly (Code? Code, Rarity Rarity)?[] _last = new (Code?, Rarity)?[16];
    private string? _kinds;
    // Each cell's Inmate as last read before the red screen (its Code must match for its rarity to hold).
    private readonly (Code? Code, Rarity Rarity)?[] _before = new (Code?, Rarity)?[16];
    // A cell read lower than before once: a second such reading in a row is a new Inmate, not the red.
    private readonly bool[] _lowerOnce = new bool[16];

    /// <summary>
    /// The Sandbox read whole off the red screen: the rarities no reading on it may drop below. Only the Warden's cell
    /// may hold a lower one now (a Replace); elsewhere one lower reading leaves the higher one standing, as the red's
    /// first frame can come before its banner reads. Two in a row are taken: another Inmate of the same Code took the
    /// cell (2026-10-27: a Lion Ultimate became Chromatic, a Lion Magic came, and the red screen showed it Ultimate).
    /// </summary>
    public void Before(EditableSandbox b)
    {
        for (int n = 2; n <= 16; n++)
        {
            var c = b[n];
            if (c.Covered) continue;
            bool lower = n != b.Warden && _before[n - 1] is { } was && was.Code == c.Code && was.Rarity > c.Rarity;
            if (c.Kind == CellKind.Inmate && lower && !_lowerOnce[n - 1])
            {
                _lowerOnce[n - 1] = true;
                continue;
            }
            _before[n - 1] = c.Kind == CellKind.Inmate ? (c.Code, c.Rarity) : null;
            _lowerOnce[n - 1] = false;
        }
    }

    public void Clear()
    {
        foreach (var v in _votes) v.Clear();
        Array.Clear(_last);
        _kinds = null;
    }

    /// <summary>Adds this frame's readings of <paramref name="b"/>'s Inmates and sets each to its most read Code and rarity.</summary>
    public void Apply(EditableSandbox b)
    {
        string kinds = string.Concat(Enumerable.Range(2, 15).Select(n => b[n].Kind == CellKind.Inmate || b[n].Covered ? 'I' : 'o'));
        if (kinds != _kinds)
        {
            Clear();
            _kinds = kinds;
        }
        for (int n = 2; n <= 16; n++)
        {
            var c = b[n];
            if (c.Kind != CellKind.Inmate || c.Covered) continue;
            if (_before[n - 1] is { } was && was.Code == c.Code && was.Rarity > c.Rarity) c.Rarity = was.Rarity;
            var read = (c.Code, c.Rarity);
            var votes = _votes[n - 1];
            votes[read] = votes.GetValueOrDefault(read) + 1;
            _last[n - 1] = read;
            // The most read; a tie goes to this frame's reading.
            var best = votes.MaxBy(kv => (kv.Value, kv.Key == read ? 1 : 0)).Key;
            (c.Code, c.Rarity) = best;
        }
    }
}
