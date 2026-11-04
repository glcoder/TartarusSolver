using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Tartarus.App.Localization;
using Tartarus.Vision;
using Tartarus.Vision.Ocr;

namespace Tartarus.App;

/// <summary>
/// The interface's language. The lines are .NET resources (Localization/Strings.resx in English, a Strings.ru.resx... per
/// language, read through <see cref="Strings"/>): a line a language doesn't have shows in English, never blank. The game's own
/// names are the game's (models/text/tartarus_text.json), the rest is machine translated; diagnostic logs stay English.
/// Started from the Simplified Chinese version by xingkinglb (https://github.com/xingkinglb/TartarusSolver-zh).
/// </summary>
public static class Loc
{
    /// <summary>The language of Windows, kept before <see cref="Init"/> sets the app's own.</summary>
    private static readonly string WindowsCulture = CultureInfo.CurrentUICulture.Name;

    /// <summary>The language the interface is drawn in, as the game names it: "en", "ru", "zh", "zhTw"... Set once at start-up.</summary>
    public static string Language { get; private set; } = "en";

    /// <summary><paramref name="setting"/>: the player's choice, a language or "auto" (or nothing): the language of Windows.</summary>
    public static void Init(string? setting)
    {
        Language = Resolve(setting);
        var culture = Culture(Language);
        // Every thread: a status or a hint line may be made off the UI thread.
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>The language a setting stands for: Windows's for "auto"; a language the game doesn't have is English.</summary>
    public static string Resolve(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting) || setting.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return OcrPacks.FromCulture(WindowsCulture);
        return OcrPacks.GameLanguages.FirstOrDefault(code => code.Equals(setting, StringComparison.OrdinalIgnoreCase))
            ?? (setting.Equals("zhCN", StringComparison.OrdinalIgnoreCase) ? "zh" : "en");
    }

    private static CultureInfo Culture(string language) => CultureInfo.GetCultureInfo(language switch
    {
        "zh" => "zh-Hans",
        "zhTw" => "zh-Hant",
        "en" => "en-US",
        var code => code,
    });

    /// <summary>A line with {0}, {1}… filled in, numbers as the interface has them ("12,800", "6.4").</summary>
    public static string F(string format, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, format, args);

    /// <summary>A line in another language than the interface's, by its key (<c>nameof(Strings.Language_Choose)</c>).</summary>
    public static string In(string? setting, string key) =>
        Strings.ResourceManager.GetString(key, Culture(Resolve(setting))) ?? key;

    /// <summary>
    /// A name known only at run time (a Torture Instrument, a Code, a rarity), from its English: its key is <see cref="UiNames.Key"/>
    /// of it. A name without one stays English.
    /// </summary>
    public static string Name(string english) => Strings.ResourceManager.GetString(UiNames.Key(english)) ?? english;

    /// <summary>A game name in two lines for a caption: "Filthy Claws" over "Alpha Wolf" (the game's names split at ": " or "：").</summary>
    public static string Caption(string name) =>
        Name(name).Replace("：", "：\n", StringComparison.Ordinal).Replace(": ", "\n", StringComparison.Ordinal);

    /// <summary>WPF text: the language's own face first with Segoe UI after it, so a mixed line ("绮彩 ×2.0") draws whole.</summary>
    public static FontFamily Font => new(Language switch
    {
        "zh" => "Microsoft YaHei UI, Segoe UI",
        "zhTw" => "Microsoft JhengHei UI, Segoe UI",
        "ja" => "Yu Gothic UI, Meiryo UI, Segoe UI",
        "ko" => "Malgun Gothic, Segoe UI",
        "th" => "Leelawadee UI, Tahoma, Segoe UI",
        _ => "Segoe UI",
    });

    /// <summary>A built window's face and language; the language picks the right forms of the characters Chinese and Japanese share.</summary>
    public static void Apply(Window window)
    {
        if (Language == "en") return;
        window.Language = XmlLanguage.GetLanguage(Culture(Language).Name);
        window.FontFamily = Font;
    }
}
