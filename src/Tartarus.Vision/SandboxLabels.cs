using System.Text.Json.Nodes;

namespace Tartarus.Vision;

// Terms follow the game's own rules screens: the Sandbox of 16 cells (cell 1 is the Interrogation Room),
// Inmates with a Code (Mammoth, Lion, Wolf, Rat) and a Rarity (Normal, Magic, Rare, Legendary, Ultimate,
// Chromatic), Unopened and Opened cells, Destinations, Decisions, Prison Incidents, Torture Instruments.

/// <summary>
/// Hand labels for one screenshot, as stored in a dataset's truth.json. Cells are numbered 1-16 (1 is the
/// Interrogation Room); inmates are "Code/Rarity" by name ("Wolf/Rare"). Destinations are listed in card order,
/// left to right.
/// </summary>
public sealed class SandboxLabels
{
    public Dictionary<string, string> Inmates { get; set; } = [];
    public List<int> Unopened { get; set; } = [];
    public List<int>? Covered { get; set; }
    public List<int> Nests { get; set; } = [];
    /// <summary>Cells with a Prison Incident ("!").</summary>
    public List<int> Incidents { get; set; } = [];
    /// <summary>Consumable targets: Inmates marked with the red jaws.</summary>
    public List<int> Consumable { get; set; } = [];
    public int Warden { get; set; }
    /// <summary>"destination" (Select a Destination: cards to go to) or "decision" (Make Your Decision).</summary>
    public string Phase { get; set; } = Phases.Decision;
    public List<int> Destinations { get; set; } = [];
    /// <summary>Remaining Days.</summary>
    public int? Days { get; set; }
    /// <summary>Torture Instruments held, by id.</summary>
    public List<string> Instruments { get; set; } = [];
    /// <summary>Chromatic Inmates contained (at most 10).</summary>
    public int Chromatic { get; set; }
    /// <summary>Unopened cells whose Inmate's rarity shows, "cell": rarity.</summary>
    public Dictionary<string, Rarity>? VisibleRarity { get; set; }
    /// <summary>Unopened cells with Scratch Marks (Filthy Claws).</summary>
    public List<int>? ScratchMarks { get; set; }
    /// <summary>Inmates with a Tusk mark (Iron Tongs of Lust: Mammoth): Replacing one raises 4 random Inmates a tier.</summary>
    public List<int>? Tusk { get; set; }
    /// <summary>Inmates with a Prey mark (Blood-Stained Trap's Hunt): it and its Lion rise a tier each morning.</summary>
    public List<int>? Prey { get; set; }
    /// <summary>The run's theme (Tyranny, Opulence, Iron Law, Cage Fight) by id, if it has one.</summary>
    public string? Theme { get; set; }
    public bool? Objective { get; set; }

    /// <summary>
    /// Brings labels written before the game's terms were adopted (scan-truth.json's keys: locked, Prison Incidents,
    /// jaws, doors, tools, shown, scratched; codes in lower case or "elephant"; rarities as numbers or "Supreme";
    /// phases patrol / manage) up to date, in place.
    /// </summary>
    public static JsonObject Upgrade(JsonObject o)
    {
        foreach (var (from, to) in new[] { ("locked", "unopened"), ("events", "incidents"), ("jaws", "consumable"), ("doors", "destinations"), ("tools", "instruments"), ("shown", "visibleRarity"), ("scratched", "scratchMarks") })
        {
            if (!o.ContainsKey(from)) continue;
            var value = o[from];
            o.Remove(from);
            if (!o.ContainsKey(to)) o[to] = value;
        }
        if (o["phase"]?.GetValue<string>() is { } phase) o["phase"] = phase switch { "patrol" => Phases.Destination, "manage" => Phases.Decision, _ => phase };
        if (o["inmates"] is JsonObject inmates)
            foreach (var key in inmates.Select(kv => kv.Key).ToList())
                if (inmates[key]?.GetValue<string>() is { } s)
                    inmates[key] = InmateText.Format(InmateText.Parse(s));
        if (o["visibleRarity"] is JsonObject visible)
            foreach (var key in visible.Select(kv => kv.Key).ToList())
                if (Rarities.Parse(visible[key]?.ToString()) is { } r) visible[key] = r.ToString();
        return o;
    }
}

public static class Phases
{
    /// <summary>Select a Destination: the day starts by picking a cell to go to.</summary>
    public const string Destination = "destination";
    /// <summary>Make Your Decision: Consume, Replace or End (Instigate or Bestow in the Interrogation Room).</summary>
    public const string Decision = "decision";
}

/// <summary>
/// A cell: Unopened (its door shut), Inmate (opened, an Inmate inside), Empty (opened, nobody inside, as after
/// an Inmate turned Chromatic and left the Sandbox), or the Interrogation Room (cell 1, never holds an Inmate).
/// A devoured Inmate's cell reverts to Unopened.
/// </summary>
public enum CellKind { Empty, Unopened, Inmate, InterrogationRoom }

/// <summary>An Inmate's Code. The devouring relationship: Mammoth → Lion → Wolf → Rat → Mammoth.</summary>
public enum Code { Rat, Wolf, Lion, Mammoth }

/// <summary>An Inmate's rarity, as the rules name them. Chromatic leaves the Sandbox at once.</summary>
public enum Rarity { Normal, Magic, Rare, Legendary, Ultimate, Chromatic }

public static class Codes
{
    public static readonly Code[] All = [Code.Rat, Code.Wolf, Code.Lion, Code.Mammoth];

    /// <summary>
    /// A Code by name in any case, or by the names other sources use: "elephant" (a rules screen's translation),
    /// "mammoth" in lower case (scan.js, older labels). Null for anything else ("ghost", "unknown", "?").
    /// </summary>
    public static Code? Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "rat" => Code.Rat,
        "wolf" => Code.Wolf,
        "lion" => Code.Lion,
        "mammoth" or "elephant" => Code.Mammoth,
        _ => null,
    };

    public static string Name(Code? code) => code?.ToString() ?? "?";
}

public static class Rarities
{
    /// <summary>A rarity by name in any case, by number (0 Normal .. 5 Chromatic), or by older names (Supreme, Rainbow).</summary>
    public static Rarity? Parse(string? s)
    {
        if (int.TryParse(s, out int n)) return n is >= 0 and <= 5 ? (Rarity)n : null;
        return s?.Trim().ToLowerInvariant() switch
        {
            "normal" or "common" => Rarity.Normal,
            "magic" => Rarity.Magic,
            "rare" => Rarity.Rare,
            "legendary" => Rarity.Legendary,
            "ultimate" or "supreme" or "epic" => Rarity.Ultimate,
            "chromatic" or "rainbow" or "prismatic" => Rarity.Chromatic,
            _ => null,
        };
    }
}

/// <summary>An Inmate as labels write it: "Code/Rarity" ("Wolf/Rare"), "?" for a Code that can't be told.</summary>
public static class InmateText
{
    public static string Format((Code? Code, Rarity Rarity) inmate) => $"{Codes.Name(inmate.Code)}/{inmate.Rarity}";

    public static (Code? Code, Rarity Rarity) Parse(string s)
    {
        var parts = s.Split('/');
        return (Codes.Parse(parts[0]), parts.Length > 1 ? Rarities.Parse(parts[1]) ?? Rarity.Normal : Rarity.Normal);
    }
}

/// <summary>One cell's label, editable in the overlay.</summary>
public sealed class CellLabel
{
    /// <summary>1-16.</summary>
    public int N { get; init; }
    public CellKind Kind { get; set; }
    /// <summary>Null when the Code can't be told.</summary>
    public Code? Code { get; set; }
    public Rarity Rarity { get; set; }
    /// <summary>A Consumable target (the red jaws over the portrait).</summary>
    public bool Consumable { get; set; }
    public bool Nest { get; set; }
    /// <summary>A Prison Incident ("!") on this cell.</summary>
    public bool Incident { get; set; }
    /// <summary>An Unopened cell showing its Inmate's rarity: that rarity, else null.</summary>
    public Rarity? VisibleRarity { get; set; }
    /// <summary>An Unopened cell with Scratch Marks (Filthy Claws): a Wolf of that rarity can consume through it.</summary>
    public bool ScratchMarks { get; set; }
    /// <summary>An Inmate with a Tusk mark (Iron Tongs of Lust: Mammoth).</summary>
    public bool Tusk { get; set; }
    /// <summary>An Inmate with a Prey mark (a Hunt of Blood-Stained Trap: Berserk Lion).</summary>
    public bool Prey { get; set; }
    /// <summary>Hidden behind UI; not scored.</summary>
    public bool Covered { get; set; }
    /// <summary>Changed by hand since the scan.</summary>
    public bool Edited { get; set; }

    public CellLabel Clone() => (CellLabel)MemberwiseClone();
}

/// <summary>Mutable labels of the whole Sandbox, made from a scan and then corrected by hand.</summary>
public sealed class EditableSandbox
{
    public CellLabel[] Cells { get; private init; } = new CellLabel[16];
    /// <summary>1-16, 0 when unknown.</summary>
    public int Warden { get; set; }
    /// <summary>1-based cell numbers, card order.</summary>
    public List<int> Destinations { get; set; } = [];
    public int? Days { get; set; }
    public List<string> Instruments { get; set; } = [];
    public int Chromatic { get; set; }
    public string Phase { get; set; } = Phases.Decision;
    public bool? Objective { get; set; }
    /// <summary>The run's theme by id (<see cref="RunThemes"/>), if it has one.</summary>
    public string? Theme { get; set; }
    /// <summary>Warden, Destinations or other Sandbox-level fields changed by hand.</summary>
    public bool SandboxEdited { get; set; }

    public CellLabel this[int n] => Cells[n - 1];

    public bool AnyEdited => SandboxEdited || Cells.Any(c => c.Edited);

    public static EditableSandbox Empty()
    {
        var b = new EditableSandbox();
        for (int i = 0; i < 16; i++) b.Cells[i] = new CellLabel { N = i + 1, Kind = i == 0 ? CellKind.InterrogationRoom : CellKind.Empty };
        return b;
    }

    public static EditableSandbox FromScan(ScanResult r)
    {
        var b = Empty();
        if (!r.Ok || r.Cells is null) return b;
        foreach (var c in r.Cells)
        {
            if (c.N < 0 || c.N > 15) continue;
            var cell = b.Cells[c.N];
            cell.Covered = c.Kind == CellRead.Covered;
            cell.Kind = c.Kind switch
            {
                CellRead.InterrogationRoom => CellKind.InterrogationRoom,
                CellRead.Unopened => CellKind.Unopened,
                CellRead.Inmate => CellKind.Inmate,
                _ => CellKind.Empty,
            };
            if (cell.Kind == CellKind.Inmate)
            {
                cell.Code = c.Code;
                cell.Rarity = c.Rarity ?? Rarity.Normal;
                cell.Consumable = c.Consumable == true;
                cell.Tusk = c.Tusk;
                cell.Prey = c.Prey;
            }
            cell.Nest = c.Nest && cell.Kind == CellKind.Unopened;
            cell.Incident = c.Incident;
            if (cell.Kind == CellKind.Unopened && c.VisibleRarity is { } visible)
            {
                cell.VisibleRarity = visible.Rarity;
                cell.ScratchMarks = visible.ScratchMarks;
            }
        }
        b.Warden = r.Warden is { } w ? w.Cell + 1 : 0;
        b.Destinations = r.Destinations?.Cells.Select(c => c + 1).ToList() ?? [];
        b.Phase = r.Destinations is null ? Phases.Decision : Phases.Destination;
        b.Days = r.Days?.Days;
        b.Instruments = r.Instruments?.Instruments.Select(t => t.Id).ToList() ?? [];
        b.Chromatic = r.Chromatic?.Count ?? 0;
        return b;
    }

    public static EditableSandbox FromLabels(SandboxLabels l)
    {
        var b = Empty();
        for (int n = 2; n <= 16; n++)
        {
            var cell = b[n];
            if (l.Unopened.Contains(n)) cell.Kind = CellKind.Unopened;
            else if (l.Inmates.TryGetValue(n.ToString(), out var s))
            {
                cell.Kind = CellKind.Inmate;
                (cell.Code, cell.Rarity) = InmateText.Parse(s);
            }
            cell.Covered = l.Covered?.Contains(n) == true;
            cell.Nest = l.Nests.Contains(n);
            cell.Incident = l.Incidents.Contains(n);
            cell.Consumable = l.Consumable.Contains(n);
            cell.Tusk = l.Tusk?.Contains(n) == true;
            cell.Prey = l.Prey?.Contains(n) == true;
            if (cell.Kind == CellKind.Unopened)
            {
                cell.VisibleRarity = l.VisibleRarity != null && l.VisibleRarity.TryGetValue(n.ToString(), out var vr) ? vr : null;
                cell.ScratchMarks = l.ScratchMarks?.Contains(n) == true;
            }
        }
        b.Warden = l.Warden;
        b.Destinations = [.. l.Destinations];
        b.Days = l.Days;
        b.Instruments = [.. l.Instruments];
        b.Chromatic = l.Chromatic;
        b.Phase = l.Phase;
        b.Objective = l.Objective;
        b.Theme = l.Theme;
        return b;
    }

    public SandboxLabels ToLabels()
    {
        var l = new SandboxLabels
        {
            Warden = Warden,
            Phase = Phase,
            Destinations = [.. Destinations],
            Days = Days,
            Instruments = [.. Instruments],
            Chromatic = Chromatic,
            Objective = Objective,
            Theme = Theme,
        };
        var covered = new List<int>();
        for (int n = 2; n <= 16; n++)
        {
            var c = this[n];
            if (c.Covered) covered.Add(n);
            switch (c.Kind)
            {
                case CellKind.Unopened:
                    l.Unopened.Add(n);
                    if (c.Nest) l.Nests.Add(n);
                    if (c.VisibleRarity is { } visible) (l.VisibleRarity ??= [])[n.ToString()] = visible;
                    if (c.ScratchMarks) (l.ScratchMarks ??= []).Add(n);
                    break;
                case CellKind.Inmate:
                    l.Inmates[n.ToString()] = InmateText.Format((c.Code, c.Rarity));
                    if (c.Consumable) l.Consumable.Add(n);
                    if (c.Tusk) (l.Tusk ??= []).Add(n);
                    if (c.Prey) (l.Prey ??= []).Add(n);
                    break;
            }
            if (c.Incident && c.Kind is CellKind.Unopened or CellKind.Inmate) l.Incidents.Add(n);
        }
        if (covered.Count > 0) l.Covered = covered;
        return l;
    }

    /// <summary>
    /// Cells the game's UI covers now take what <paramref name="previous"/> read there (still marked Covered), so a
    /// tooltip doesn't wipe a cell out. <paramref name="previous"/> must be the same Sandbox, a moment earlier.
    /// </summary>
    public void KeepCovered(EditableSandbox previous)
    {
        for (int n = 2; n <= 16; n++)
        {
            var c = this[n];
            if (!c.Covered) continue;
            var p = previous[n];
            (c.Kind, c.Code, c.Rarity, c.Consumable, c.Nest, c.Incident, c.VisibleRarity, c.ScratchMarks, c.Tusk, c.Prey) =
                (p.Kind, p.Code, p.Rarity, p.Consumable, p.Nest, p.Incident, p.VisibleRarity, p.ScratchMarks, p.Tusk, p.Prey);
        }
    }

    /// <summary>
    /// A cell's new reading counts once two scans in a row agree on it: until then the cell keeps what
    /// <paramref name="shown"/> had. One odd frame (an animation, a glow) doesn't flip a cell; a real change shows a
    /// scan later. This is the newest reading; <paramref name="previousRead"/> the one before, as read. The pulsing
    /// Consumable mark isn't compared (the app sets it from the rules), and a cell the last one showed covered has
    /// nothing to keep: it takes its first reading.
    /// </summary>
    public void Steady(EditableSandbox shown, EditableSandbox previousRead)
    {
        for (int n = 2; n <= 16; n++)
        {
            if (Reading(this[n]) == Reading(previousRead[n]) || shown[n].Covered) continue;
            var (c, s) = (this[n], shown[n]);
            (c.Kind, c.Code, c.Rarity, c.Consumable, c.Nest, c.Incident, c.VisibleRarity, c.ScratchMarks, c.Tusk, c.Prey, c.Covered) =
                (s.Kind, s.Code, s.Rarity, s.Consumable, s.Nest, s.Incident, s.VisibleRarity, s.ScratchMarks, s.Tusk, s.Prey, s.Covered);
        }

        static string Reading(CellLabel c) => c.Consumable ? SandboxText.CellShort(c).Replace(" +Consumable", "") : SandboxText.CellShort(c);
    }

    /// <summary>
    /// The cells as <paramref name="read"/> saw them, where it saw them (not covered). For a pick that comes up right
    /// after a change (an Objective done as a door opens, a Chromatic made): <see cref="Steady"/> holds the change back
    /// a scan, and the pick hides the Sandbox before that scan.
    /// </summary>
    public void TakeCells(EditableSandbox read)
    {
        for (int n = 2; n <= 16; n++)
            if (!read[n].Covered) Cells[n - 1] = read[n].Clone();
    }

    public EditableSandbox Clone()
    {
        var b = new EditableSandbox
        {
            Warden = Warden,
            Destinations = [.. Destinations],
            Days = Days,
            Instruments = [.. Instruments],
            Chromatic = Chromatic,
            Phase = Phase,
            Objective = Objective,
            Theme = Theme,
            SandboxEdited = SandboxEdited,
        };
        for (int i = 0; i < 16; i++) b.Cells[i] = Cells[i].Clone();
        return b;
    }
}
