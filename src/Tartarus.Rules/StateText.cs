using System.Text;

namespace Tartarus.Rules;

/// <summary>Text of a state, in the game's terms, for traces and tests. Cells numbered as on screen (1-16).</summary>
public static class StateText
{
    public static string Cell(in GameState s, int n)
    {
        if (n == GameData.Room) return "Interrogation Room";
        ref readonly var c = ref s.Cells[n];
        var sb = new StringBuilder();
        if (c.Sealed)
        {
            sb.Append("Unopened");
            if (c.Has(CellMarks.RarityShown) && !c.Inmate.IsNone) sb.Append(" ?").Append(c.Inmate.Rarity);
            if (c.Has(CellMarks.Scratch)) sb.Append(" +Scratch Marks");
            if (c.NestDue != 0) sb.Append($" +Rat Nest(day {c.NestDue})");
        }
        else
        {
            sb.Append(c.Inmate.Code).Append(' ').Append(c.Inmate.Rarity);
            if (c.Has(CellMarks.Tusk)) sb.Append(" +Tusk");
            for (int i = 0; i < s.PreyCount; i++) if (s.Prey[i].Prey == c.Inmate.Id) sb.Append(" +Prey");
        }
        if (c.Marker != Incident.None) sb.Append(" !").Append(GameData.Info(c.Marker).Name);
        return sb.ToString();
    }

    public static string Describe(in GameState s)
    {
        var sb = new StringBuilder();
        sb.Append($"Day {s.Day}/{s.MaxDays} (Remaining Days {s.RemainingDays}) · {s.Phase}");
        if (s.Used != DayAction.None) sb.Append($" ({s.Used} used)");
        if (s.Replaced) sb.Append(" (Replace done)");
        sb.Append($" · Warden at {s.Position + 1} · Chromatic {s.Chromatic}/{GameData.ChromaticCap}");
        if (s.Theme != Theme.None) sb.Append($" · {GameData.Name(s.Theme)}");
        sb.Append($" · Objective {GameData.Name(s.Objective)} {s.ObjectiveProgress}/{GameData.Target(s.Objective)}{(s.ObjectiveDone ? " done" : "")}");
        sb.AppendLine();
        var held = new List<string>();
        for (int i = 0; i < GameData.InstrumentCount; i++) if (s.Has((Instrument)i)) held.Add(GameData.Info((Instrument)i).Name);
        sb.AppendLine($"Torture Instruments: {(held.Count > 0 ? string.Join(", ", held) : "-")} · Re-rolls {s.Rerolls}");
        for (int n = 1; n < GameData.Cells; n++) sb.Append($"{n + 1:00} {Cell(s, n)}{(n == s.Position ? " [W]" : "")} | ");
        sb.AppendLine();
        if (s.DestinationCount > 0)
        {
            sb.Append("Destinations:");
            for (int i = 0; i < s.DestinationCount; i++) sb.Append(' ').Append(s.Destinations[i] == GameData.Room ? "Room" : (s.Destinations[i] + 1).ToString());
            sb.AppendLine();
        }
        for (int i = 0; i < s.PendingCount; i++) sb.AppendLine($"Pending: {Pending(s, s.Pending[i])}");
        return sb.ToString();
    }

    public static string Pending(in GameState s, Pending p) => p.Kind switch
    {
        PendingKind.Instrument => "Select a Torture Instrument: " + string.Join(" | ", Enumerable.Range(0, p.Count).Select(i => GameData.Info(p.Offer(i)).Name)),
        PendingKind.Treasure => $"Warden's Treasure ({p.Picks} pick(s)): " + string.Join(" | ", Enumerable.Range(0, 4).Where(t => (p.A & (1 << t)) != 0).Select(t => GameData.Name((Treasure)t))),
        PendingKind.Interrogation => "Interrogation Room: Instigate or Bestow",
        PendingKind.Chain => $"Chain Consume by #{p.Eater}",
        PendingKind.Ratway => "Sewer Ratway: pick a cell",
        _ => "-",
    };
}
