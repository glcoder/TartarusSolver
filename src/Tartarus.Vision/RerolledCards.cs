namespace Tartarus.Vision;

/// <summary>
/// The cards a Re-roll just clicked is taking away: they stay on screen a moment, and a hint on them would flash
/// (2026-11-01: one of the old three hinted after the click). The new offer may hold the very same cards
/// (2 of 320 Re-rolls in players' logs, both in another order; 2026-11-05: no hint at all until the pick was taken), so
/// the cards in another order are the new offer at once, and the same order is taken for it after <see cref="Limit"/>.
/// </summary>
public sealed class RerolledCards
{
    /// <summary>Click to other cards read: 0.7-1.6 s in 9 of 10 Re-rolls measured (8.8 s once, the old ones gone after 0.4 s). Hardly ever reached: only the same three in the same order wait so long.</summary>
    public static TimeSpan Limit { get; } = TimeSpan.FromSeconds(3);

    private IReadOnlyList<string>? _old;
    private DateTime _clickedAt;

    public void Clicked(IReadOnlyList<string> old, DateTime now) => (_old, _clickedAt) = (old, now);

    /// <summary>Are the cards read (<paramref name="known"/> of <paramref name="cards"/> on screen, left to right) still the old ones going away?</summary>
    public bool StillGoing(IReadOnlyList<string> known, int cards, DateTime now)
    {
        if (_old is not { } old) return false;
        bool going = known.Count > 0 && now - _clickedAt < Limit
            && (known.Count == cards ? known.SequenceEqual(old) : known.All(old.Contains));
        if (!going) _old = null;
        return going;
    }
}
