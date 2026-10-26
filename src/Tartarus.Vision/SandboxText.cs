using System.Text;

namespace Tartarus.Vision;

/// <summary>Short text summaries of a Sandbox, in the game's terms, for logs and the CLI.</summary>
public static class SandboxText
{
    /// <summary>"Wolf Rare +Consumable", "Unopened ?Magic +Scratch Marks", "Empty", ...</summary>
    public static string CellShort(CellLabel c) => (c.Covered ? "Covered: " : "") + c.Kind switch
    {
        CellKind.InterrogationRoom => "Interrogation Room",
        CellKind.Unopened => "Unopened" + (c.VisibleRarity is { } r ? $" ?{r}" : "") + (c.ScratchMarks ? " +Scratch Marks" : "") + (c.Nest ? " +Rat Nest" : "") + (c.Incident ? " +Incident" : ""),
        CellKind.Inmate => $"{Codes.Name(c.Code)} {c.Rarity}" + (c.Consumable ? " +Consumable" : "") + (c.Tusk ? " +Tusk" : "") + (c.Prey ? " +Prey" : "") + (c.Incident ? " +Incident" : ""),
        _ => "Empty",
    };

    public static string Describe(EditableSandbox b)
    {
        var sb = new StringBuilder();
        for (int n = 2; n <= 16; n++) sb.Append($"{n:00}: {CellShort(b[n])} | ");
        sb.AppendLine();
        sb.Append($"Warden {(b.Warden > 0 ? b.Warden.ToString() : "?")} · Destinations [{string.Join(",", b.Destinations)}] · Remaining Days {b.Days?.ToString() ?? "?"} · Torture Instruments [{string.Join(",", b.Instruments)}] · Chromatic {b.Chromatic}");
        return sb.ToString();
    }

    /// <summary>The game's name of a pick card's id: an instrument's or a treasure's, by the pick's kind.</summary>
    public static string PickName(PickRead pick, string id) =>
        pick.Kind == PickRead.Treasure ? WardenTreasures.NameOf(id) : TortureInstruments.NameOf(id);

    public static string Describe(ScanResult r)
    {
        if (r.Theme is { } theme) return $"Run theme: {RunThemes.NameOf(theme)}";
        if (r.Pick is { } pick)
            return (pick.Kind == PickRead.Treasure ? "Warden's Treasure: " : "Select a Torture Instrument: ")
                + string.Join(" | ", pick.Cards.Select(c => c.Id != null ? PickName(pick, c.Id) : $"? \"{c.Text}\""))
                + (pick.Rerolls is int n ? $" · Re-roll {n}" : "");
        if (!r.Ok) return $"no Sandbox: {r.Reason}";
        var sb = new StringBuilder();
        sb.Append($"Sandbox s={r.Loc?.S:0.000} presence={r.Loc?.Score:0.000} · {r.Ms:0} ms");
        sb.AppendLine();
        sb.Append(Describe(EditableSandbox.FromScan(r)));
        if (r.Notes is { Count: > 0 }) sb.AppendLine().Append("notes: ").Append(string.Join(" | ", r.Notes));
        return sb.ToString();
    }
}
