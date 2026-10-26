namespace Tartarus.Vision;

/// <summary>
/// On the Consume target pick (the red screen) nothing on the Sandbox changes until a bite, while the red flickers
/// over it (the glows, the target's jaws, the small mouth over each eater). Each Inmate there shows what its readings
/// over the pick's frames say most often, so one frame's misread doesn't flip it. The votes start over when the pick
/// ends or the cells' kinds change (a bite empties its meal's cell, Avenger's Casket opens one).
/// </summary>
public sealed class PickVotes
{
    private readonly Dictionary<(Code? Code, Rarity Rarity), int>[] _votes = Enumerable.Range(0, 16).Select(_ => new Dictionary<(Code?, Rarity), int>()).ToArray();
    private readonly (Code? Code, Rarity Rarity)?[] _last = new (Code?, Rarity)?[16];
    private string? _kinds;

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
