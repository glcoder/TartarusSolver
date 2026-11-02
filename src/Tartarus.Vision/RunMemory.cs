using R = Tartarus.Rules;

namespace Tartarus.Vision;

/// <summary>
/// What a run has gathered, kept from scan to scan, for the reads a tooltip, a fade or an animation can spoil: the
/// Torture Instruments held and the Chromatic count. Both only ever grow within a run. A new run (Remaining Days
/// going up) starts over. Also the run's theme, which shows once, in the lobby before the run.
/// </summary>
public sealed class RunMemory
{
    private readonly List<string> _held = [];
    // Another instrument read in a held slot, and how many scans in a row: a slot's instrument doesn't change in a run,
    // but a frame mid-fade reads one wrong (2026-11-01: Kin's Coin of Fate read as Ravenous Chains as a Warden's
    // Treasure opened, and the pick was hinted for the wrong instruments).
    private readonly Dictionary<int, (string Id, int Times)> _otherRead = [];
    /// <summary>Reads in a row it takes to change what a run has settled (a held slot's instrument, the Objective).</summary>
    public const int ChangeAfter = 3;
    // The run's Objective (kind, progress, done), and another one read instead, how many times in a row.
    private (R.ObjectiveKind Kind, int Progress, bool Done)? _objective;
    private (R.ObjectiveKind Kind, int Times)? _otherObjective;
    private readonly HashSet<int> _kept = [];
    private int? _lastDays;
    // The Remaining Days of the last day a Consume's or Hunt's target pick was seen on.
    private int? _consumeDays;
    private int _chromatic;
    private int? _chromaticSeen;
    private DateTimeOffset _chromaticSince;
    private string? _theme;
    /// <summary>The theme was seen since the last Sandbox: it is the coming run's.</summary>
    private bool _themeComing;

    /// <summary>How long a higher Chromatic read must hold before it counts: a portrait flies into the panel in about a second.</summary>
    public static readonly TimeSpan ChromaticHold = TimeSpan.FromSeconds(1.2);

    public IReadOnlyList<string> Held => _held;
    /// <summary>Instrument slots shown from memory in the last update (their read wasn't an instrument), by index.</summary>
    public IReadOnlySet<int> Kept => _kept;
    public int Chromatic => _chromatic;
    /// <summary>The Remaining Days last read (or told by the log) in this run; null before any.</summary>
    public int? LastDays => _lastDays;
    /// <summary>The run's theme by id (<see cref="RunThemes"/>), or null.</summary>
    public string? Theme => _theme;

    /// <summary>Reads of the theme's icon in a row that make it the run's: a theme doesn't change within a run.</summary>
    public const int ThemeIconReads = 3;
    private string? _iconRead;
    private int _iconReads;

    /// <summary>Notes a plain Sandbox's theme icon (null: none read). True when this makes it the run's theme.</summary>
    public bool ThemeIcon(string? id)
    {
        _iconReads = id != null && id == _iconRead ? _iconReads + 1 : id != null ? 1 : 0;
        _iconRead = id;
        if (id == null || _iconReads < ThemeIconReads || _theme == id) return false;
        (_theme, _themeComing) = (id, false);
        return true;
    }

    /// <summary>The run is over: its theme goes, whatever screens stay up, not to reach the next run.</summary>
    public void ThemeOver() => (_theme, _themeComing, _iconRead, _iconReads) = (null, false, null, 0);

    /// <summary>
    /// Whether more Remaining Days than before means a new run. Off while the game's log tells runs apart: a misread
    /// day count would otherwise wipe the run's memory.
    /// </summary>
    public bool NewRunByDays { get; set; } = true;

    /// <summary>Notes a scan's Remaining Days: more days than before is a new run (keeping a theme seen just before it).</summary>
    public void Days(int? days)
    {
        if (days == null) return;
        if (NewRunByDays && _lastDays is int last && days > last) NewRun();
        _lastDays = days;
        _themeComing = false;
    }

    /// <summary>Notes a Consume's or Hunt's target pick, on the day with these Remaining Days.</summary>
    public void ConsumeSeen(int? days)
    {
        if (days != null) _consumeDays = days;
    }

    /// <summary>A target pick was seen on the day with these Remaining Days: for logs that don't tell (pages only, or no log).</summary>
    public bool ConsumedOn(int days) => _consumeDays == days;

    /// <summary>A new run begins: everything goes but a theme seen just before it (the lobby shows it first).</summary>
    public void NewRun()
    {
        string? theme = _themeComing ? _theme : null;
        bool coming = _themeComing;
        Clear();
        (_theme, _themeComing) = (theme, coming);
    }

    /// <summary>Notes the theme screen (in the lobby, before a run): the coming run's theme.</summary>
    public void SawTheme(string id)
    {
        _theme = id;
        _themeComing = true;
    }

    /// <summary>
    /// The instruments to show for a scan's slot reading (ids in slot order). A slot read as an instrument shows it
    /// (and is remembered); one that can't be read now (a tooltip over it: <see cref="SlotReader.Hidden"/>, the
    /// network unsure: <see cref="SlotReader.Unknown"/>, or read empty) shows what was read there before.
    /// </summary>
    public IReadOnlyList<string> Instruments(IReadOnlyList<string> read)
    {
        _kept.Clear();
        var shown = new List<string>();
        for (int i = 0; i < Math.Max(read.Count, _held.Count); i++)
        {
            string? r = i < read.Count ? read[i] : null;
            if (r != null && SlotReader.IsInstrument(r))
            {
                if (i < _held.Count && _held[i] != r)
                {
                    // Another instrument where one is held: only when read so again and again.
                    var other = _otherRead.TryGetValue(i, out var o) && o.Id == r ? (r, o.Times + 1) : (r, 1);
                    if (other.Item2 >= ChangeAfter)
                    {
                        _held[i] = r;
                        _otherRead.Remove(i);
                    }
                    else
                    {
                        _otherRead[i] = other;
                        shown.Add(_held[i]);
                        _kept.Add(i);
                        continue;
                    }
                }
                else _otherRead.Remove(i);
                if (i == _held.Count) _held.Add(r);
                shown.Add(r);
            }
            else if (i < _held.Count)
            {
                shown.Add(_held[i]);
                _kept.Add(i);
            }
            else if (r != null) shown.Add(SlotReader.Unknown);
        }
        return shown;
    }

    /// <summary>
    /// The Chromatic count to show for a scan's read of a frame captured <paramref name="at"/>: never fewer than
    /// before (a fade or a day's banner reads low), and more once the higher read has held for
    /// <see cref="ChromaticHold"/> (a portrait flying into the panel reads high for a moment). A read with slots a
    /// tooltip covers (<paramref name="covered"/>) changes nothing.
    /// </summary>
    public int ChromaticCount(int read, bool covered, DateTimeOffset at)
    {
        if (read <= _chromatic || covered)
        {
            _chromaticSeen = null;
            return _chromatic;
        }
        if (read != _chromaticSeen) (_chromaticSeen, _chromaticSince) = (read, at);
        else if (at - _chromaticSince >= ChromaticHold) _chromatic = read;
        return _chromatic;
    }

    /// <summary>
    /// The run's Objective into <paramref name="o"/>: the one read, unless another was settled and this one wasn't read
    /// <see cref="ChangeAfter"/> times in a row (a fade reads another name, 2026-11-01: Survival of the Fittest 0/4 for
    /// a done Behind Cell Doors); the settled one when none reads. <paramref name="settled"/> false: a reading not
    /// settled yet (searched ahead), told what the run has settled without counting toward a change.
    /// </summary>
    public void Objective(R.Observation o, bool settled = true)
    {
        if (!settled)
        {
            if (_objective is { } known && o.Objective != known.Kind)
                (o.Objective, o.ObjectiveProgress, o.ObjectiveDone) = (known.Kind, known.Progress, known.Done || o.ObjectiveDone);
            else if (_objective is { } same) o.ObjectiveDone |= same.Done;
            return;
        }
        if (_objective is not { } kept)
        {
            if (o.Objective is { } first) _objective = (first, o.ObjectiveProgress, o.ObjectiveDone);
            return;
        }
        if (o.Objective is null || o.Objective == kept.Kind)
        {
            _otherObjective = null;
            if (o.Objective is null) (o.Objective, o.ObjectiveProgress) = (kept.Kind, kept.Progress);
            o.ObjectiveDone |= kept.Done;
            _objective = (kept.Kind, o.ObjectiveProgress, o.ObjectiveDone);
            return;
        }
        var other = _otherObjective is { } seen && seen.Kind == o.Objective ? (seen.Kind, seen.Times + 1) : (o.Objective.Value, 1);
        if (other.Item2 >= ChangeAfter)
        {
            _objective = (other.Item1, o.ObjectiveProgress, o.ObjectiveDone);
            _otherObjective = null;
            return;
        }
        _otherObjective = other;
        (o.Objective, o.ObjectiveProgress, o.ObjectiveDone) = (kept.Kind, kept.Progress, kept.Done || o.ObjectiveDone);
    }

    public void Clear()
    {
        _consumeDays = null;
        _held.Clear();
        _otherRead.Clear();
        (_objective, _otherObjective) = (null, null);
        _kept.Clear();
        _lastDays = null;
        _chromatic = 0;
        _chromaticSeen = null;
        ThemeOver();
    }
}
