namespace Tartarus.Vision;

/// <summary>Per-field hit counts of a scan against hand labels.</summary>
public sealed class Tally
{
    public readonly Dictionary<string, (int Ok, int All)> Fields = [];

    public void Count(string field, bool ok)
    {
        Fields.TryGetValue(field, out var t);
        Fields[field] = (t.Ok + (ok ? 1 : 0), t.All + 1);
    }

    public void Add(Tally other)
    {
        foreach (var (k, v) in other.Fields)
        {
            Fields.TryGetValue(k, out var t);
            Fields[k] = (t.Ok + v.Ok, t.All + v.All);
        }
    }

    public static readonly string[] Order = ["cells", "codes", "rarity", "warden", "destinations", "days", "instruments", "incidents", "nests", "consumable"];

    public override string ToString() => string.Join(" · ", Order.Where(Fields.ContainsKey).Select(k =>
    {
        var (ok, all) = Fields[k];
        return all == 0 ? $"{k} -" : $"{k} {100.0 * ok / all:0.#}% ({ok}/{all})";
    }));
}

/// <summary>A scan against hand labels, field by field (after tartarus-simulator/tools/scan-test.js's score()).</summary>
public static class Scoring
{
    public static (Tally Tally, List<string> Errors) Score(ScanResult r, SandboxLabels t)
    {
        var tally = new Tally();
        var errs = new List<string>();
        if (!r.Ok || r.Cells is null)
        {
            errs.Add($"no Sandbox ({r.Reason}) presence {(r.Loc is { } l ? l.Score.ToString("0.000") : "-")}");
            return (tally, errs);
        }
        for (int n = 2; n <= 16; n++)
        {
            if (t.Covered?.Contains(n) == true) continue;
            var c = r.Cells[n - 1];
            t.Inmates.TryGetValue(n.ToString(), out var want);
            bool unopened = t.Unopened.Contains(n);
            bool kindOk = unopened ? c.IsUnopened : want != null ? c.IsInmate : c.Kind == CellRead.Empty;
            tally.Count("cells", kindOk);
            if (!kindOk)
                errs.Add($"cell {n}: read {c.Kind}{(c.IsInmate ? $" {c.Code}/{c.Rarity}" : "")}, want {(unopened ? CellRead.Unopened : want ?? CellRead.Empty)}");
            if (want != null && c.IsInmate)
            {
                var (code, rarity) = InmateText.Parse(want);
                if (code != null)
                {
                    bool ok = c.Code == code;
                    tally.Count("codes", ok);
                    if (!ok) errs.Add($"cell {n}: code {c.Code}, want {code}");
                }
                bool consumable = t.Consumable.Contains(n);
                bool consumableOk = (c.Consumable == true) == consumable;
                tally.Count("consumable", consumableOk);
                if (!consumableOk) errs.Add($"cell {n}: consumable {c.Consumable == true}, want {consumable}");
                bool rarityOk = c.Rarity == rarity;
                tally.Count("rarity", rarityOk);
                if (!rarityOk) errs.Add($"cell {n}: rarity {c.Rarity}, want {rarity}");
            }
            if (unopened)
            {
                bool incident = t.Incidents.Contains(n);
                tally.Count("incidents", c.Incident == incident);
                if (c.Incident != incident) errs.Add($"cell {n}: incident {c.Incident}, want {incident}");
                bool nest = t.Nests.Contains(n);
                tally.Count("nests", c.Nest == nest);
                if (c.Nest != nest) errs.Add($"cell {n}: nest {c.Nest}, want {nest}");
            }
        }
        bool wardenOk = r.Warden != null && r.Warden.Cell == t.Warden - 1;
        tally.Count("warden", wardenOk);
        if (!wardenOk) errs.Add($"warden {(r.Warden is { } w ? (w.Cell + 1).ToString() : "none")}, want {t.Warden}");
        if (t.Destinations.Count > 0 || t.Phase == Phases.Decision)
        {
            var got = r.Destinations?.Cells.Select(c => c + 1).ToList() ?? [];
            bool ok = got.SequenceEqual(t.Destinations);
            tally.Count("destinations", ok);
            if (!ok) errs.Add($"destinations [{string.Join(",", got)}], want [{string.Join(",", t.Destinations)}]");
        }
        bool daysOk = r.Days != null && r.Days.Days == t.Days;
        tally.Count("days", daysOk);
        if (!daysOk) errs.Add($"days {r.Days?.Days.ToString() ?? "none"}, want {t.Days}");
        var instruments = r.Instruments?.Instruments.Select(x => x.Id).ToList() ?? [];
        bool instrumentsOk = instruments.SequenceEqual(t.Instruments);
        tally.Count("instruments", instrumentsOk);
        if (!instrumentsOk) errs.Add($"instruments [{string.Join(",", instruments)}], want [{string.Join(",", t.Instruments)}]");
        return (tally, errs);
    }
}
