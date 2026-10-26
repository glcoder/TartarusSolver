using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tartarus.Vision;

/// <summary>Reads a game screenshot into a Sandbox. Implementations are not required to be thread-safe.</summary>
public interface ISandboxScanner : IDisposable
{
    string Name { get; }
    ScanResult Scan(Frame frame);
}

public static class ScanJson
{
    /// <summary>
    /// A scan from its JSON: ours, or js/scan.js's (and scans saved before the game's terms were adopted),
    /// whose words are brought to ours first: beast → code ("mammoth" → "Mammoth"), tier → rarity (2 → "Rare"),
    /// locked → unopened, Interrogation Room → interrogationRoom, jaws → consumable, event → incident,
    /// shown → visibleRarity, doors → destinations, tools → instruments.
    /// </summary>
    public static ScanResult Parse(string json, string source)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        Upgrade(node);
        var r = node.Deserialize(VisionJson.Default.ScanResult) ?? new ScanResult { Reason = "empty result" };
        r.RawJson = node.ToJsonString();
        r.Source = source;
        return r;
    }

    public static ScanResult Failure(string reason, string source) => new() { Ok = false, Reason = reason, Source = source };

    private static void Upgrade(JsonObject o)
    {
        Rename(o, "doors", "destinations");
        if (Rename(o, "tools", "instruments") is JsonObject instruments) Rename(instruments, "tools", "instruments");
        if (o["cells"] is not JsonArray cells) return;
        foreach (var c in cells.OfType<JsonObject>())
        {
            Rename(c, "beast", "code");
            Rename(c, "tier", "rarity");
            Rename(c, "jaws", "consumable");
            Rename(c, "event", "incident");
            if (Rename(c, "shown", "visibleRarity") is JsonObject visible)
            {
                Rename(visible, "tier", "rarity");
                Rename(visible, "scratch", "scratchMarks");
            }
            // Codes and rarities by name: "mammoth" → "Mammoth", 2 → "Rare". A Code that can't be told is left out.
            if (c.ContainsKey("code")) { if (Codes.Parse(c["code"]?.ToString()) is { } code) c["code"] = code.ToString(); else c.Remove("code"); }
            if (c["ghost"]?.GetValue<bool>() == true) c.Remove("code");
            if (Rarities.Parse(c["rarity"]?.ToString()) is { } rarity) c["rarity"] = rarity.ToString();
            if (c["visibleRarity"] is JsonObject vr && Rarities.Parse(vr["rarity"]?.ToString()) is { } vrr) vr["rarity"] = vrr.ToString();
            if (c["kind"]?.GetValue<string>() is { } kind)
                c["kind"] = kind switch { "office" => CellRead.InterrogationRoom, "locked" => CellRead.Unopened, _ => kind };
        }
    }

    // Moves o[from] to o[to] unless o already has o[to]; returns what is at o[to] then.
    private static JsonNode? Rename(JsonObject o, string from, string to)
    {
        if (o.ContainsKey(from))
        {
            var value = o[from];
            o.Remove(from);
            if (!o.ContainsKey(to)) o[to] = value;
        }
        return o[to];
    }
}
