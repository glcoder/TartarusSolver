using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tartarus.Rules;
using Tartarus.Vision;
using Tartarus.Vision.Ocr;
using Xunit;

namespace Tartarus.Rules.Tests;

/// <summary>The interface's lines (src/Tartarus.App/Localization), read as files: the tests don't reference the WPF app.</summary>
public class UiStringsTests
{
    private static readonly string Dir = FindDir();

    private static string FindDir()
    {
        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at != null; at = at.Parent)
            if (File.Exists(Path.Combine(at.FullName, "TartarusSolver.sln")))
                return Path.Combine(at.FullName, "src", "Tartarus.App", "Localization");
        throw new DirectoryNotFoundException("TartarusSolver.sln");
    }

    private static Dictionary<string, string> Read(string culture) =>
        XDocument.Load(Path.Combine(Dir, culture.Length == 0 ? "Strings.resx" : $"Strings.{culture}.resx")).Root!.Elements("data")
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);

    private static string CultureOf(string code) => code switch { "zh" => "zh-Hans", "zhTw" => "zh-Hant", _ => code };

    public static TheoryData<string> Languages()
    {
        var all = new TheoryData<string>();
        foreach (var code in OcrPacks.GameLanguages)
            if (code != "en") all.Add(CultureOf(code));
        return all;
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void A_language_has_every_line_and_no_other(string culture)
    {
        var english = Read("");
        var lines = Read(culture);
        Assert.Empty(english.Keys.Except(lines.Keys));
        Assert.Empty(lines.Keys.Except(english.Keys));
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line.Value), line.Key));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void A_translation_fills_in_what_the_english_does(string culture)
    {
        var english = Read("");
        foreach (var (key, line) in Read(culture))
            Assert.True(Holes(english[key]).SetEquals(Holes(line)), $"{key}: {line}");

        static HashSet<string> Holes(string line) => Regex.Matches(line, @"\{\d+\}").Select(m => m.Value).ToHashSet();
    }

    [Fact]
    public void The_generated_class_has_every_line()
    {
        var code = File.ReadAllText(Path.Combine(Dir, "Strings.Designer.cs"));
        var there = Regex.Matches(code, @"public static string (\w+) =>").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.True(there.SetEquals(Read("").Keys), "run tools/gen_strings.py");
    }

    [Fact]
    public void The_names_known_only_at_run_time_have_their_lines()
    {
        var english = Read("");
        var names = Enum.GetValues<Instrument>().Select(i => GameData.Info(i).Name)
            .Concat(Enum.GetValues<Treasure>().Select(GameData.Name))
            .Concat(Enum.GetValues<Theme>().Where(t => t != Theme.None).Select(GameData.Name))
            .Concat(Enum.GetValues<ObjectiveKind>().Select(GameData.Name))
            .Concat(Enum.GetValues<Incident>().Select(i => GameData.Info(i).Name))
            .Concat(Enum.GetValues<Tartarus.Rules.Rarity>().Select(r => r.ToString()))
            .Concat(Enum.GetValues<Tartarus.Vision.Code>().Select(c => Codes.Name(c)));
        var missing = names.Where(name => name.Length > 0 && (!english.TryGetValue(UiNames.Key(name), out var line) || line != name)).ToList();
        Assert.True(missing.Count == 0, string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void A_refused_capture_names_the_service_to_check(string culture) =>
        Assert.Contains("CaptureService", Read(culture)["Status_CaptureRefused"]);

    [Theory]
    [InlineData("Rusty Saw", "Name_RustySaw")]
    [InlineData("Warden's Cache", "Name_WardensCache")]
    [InlineData("Iron Tongs of Lust: Mammoth", "Name_IronTongsOfLustMammoth")]
    [InlineData("\"Treasure\" Hunt", "Name_TreasureHunt")]
    [InlineData("Re-roll", "Name_ReRoll")]
    public void A_run_time_name_gives_its_key(string name, string key) => Assert.Equal(key, UiNames.Key(name));
}
