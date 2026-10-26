using System.Text.Json;
using System.Text.RegularExpressions;
using Tartarus.Vision.Ocr;

namespace Tartarus.Vision;

/// <summary>
/// The game's own Tartarus texts in every language it ships (models/text/tartarus_text.json, made by
/// tools/train/gametext.py), as plain text: what the screen says matched against what the game would say, whatever its
/// language. Missing file: English only, as the program's own names have it.
/// </summary>
public static partial class GameText
{
    private static readonly Lazy<JsonDocument?> Doc = new(Load);

    private static JsonDocument? Load()
    {
        try
        {
            string? dir = NameMatch.FindModelDir("text", "tartarus_text.json");
            return dir == null ? null : JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "tartarus_text.json")));
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    public static bool Loaded => Doc.Value != null;

    /// <summary>Markup out (&lt;RichText ...&gt;, &lt;Img ...&gt;), "\n" as a space.</summary>
    public static string Plain(string text) => Spaces().Replace(Markup().Replace(text.Replace("\\n", " "), ""), " ").Trim();

    /// <summary>Every language's text of a field of the entries in a section ("instruments", "name"), by the program's id.</summary>
    public static IReadOnlyList<(string Id, string Text)> Names(string section, string field = "name") =>
        NamesCache.GetOrAdd((section, field), key => ReadNames(key.Section, key.Field));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Section, string Field), IReadOnlyList<(string Id, string Text)>> NamesCache = new();

    private static List<(string Id, string Text)> ReadNames(string section, string field)
    {
        var list = new List<(string, string)>();
        if (Doc.Value is not { } doc || !doc.RootElement.TryGetProperty(section, out var entries)) return list;
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
            if (!entry.TryGetProperty(field, out var texts)) continue;
            foreach (var t in texts.EnumerateObject()) list.Add((id.GetString()!, Plain(t.Value.GetString() ?? "")));
        }
        return list;
    }

    /// <summary>Every language's text of an interface line (TextTable_GameFunc key, "Func_S16GamePlay_UnderTitle_Choose").</summary>
    public static List<string> Interface(string key) => Texts("interface", key, null);

    /// <summary>Every language's text of a board word (s16_gameplay_config desc id, 10201 "Go to Cell %s").</summary>
    public static List<string> Board(int id) => Texts("board", id.ToString(), "desc");

    private static List<string> Texts(string section, string key, string? field)
    {
        var list = new List<string>();
        if (Doc.Value is not { } doc || !doc.RootElement.TryGetProperty(section, out var s) || !s.TryGetProperty(key, out var e)) return list;
        if (field != null && !e.TryGetProperty(field, out e)) return list;
        foreach (var t in e.EnumerateObject()) if (t.Value.GetString() is { Length: > 0 } v) list.Add(Plain(v));
        return list;
    }

    /// <summary>How alike a read line is to any of <paramref name="texts"/> (letters and digits only), 0-1.</summary>
    public static double Likeness(string read, IEnumerable<string> texts) =>
        texts.Select(t => NameMatch.Best(read, [t], x => x).Score).DefaultIfEmpty(0).Max();

    /// <summary>The words before a "%s" in each language ("Go to Cell", "Пойти в №"), normalized: a Destination card starts with one.</summary>
    public static IReadOnlyList<string> Prefixes(int boardId) => PrefixCache.GetOrAdd(boardId, id =>
        Board(id).Select(t => ScreenText.Normalize(t.Split("%s")[0])).Where(p => p.Length > 0).Distinct().ToList());

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, IReadOnlyList<string>> PrefixCache = new();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Markup();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
