using System.IO;
using System.Text.Json;

namespace Tartarus.App;

/// <summary>User settings, stored as JSON in %LOCALAPPDATA%\TartarusSolver\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Regex for the target window's title; the game by default.</summary>
    public string WindowTitle { get; set; } = @"^Torchlight: Infinite\s*$";
    public string DatasetDir { get; set; } = DefaultDatasetDir();
    public bool AutoScan { get; set; }
    public double AutoScanSeconds { get; set; } = 0.5;
    public bool OverlayVisible { get; set; } = true;
    /// <summary>Hide the overlay while neither the game nor this app is in front.</summary>
    public bool HideWhenGameInactive { get; set; } = true;
    /// <summary>Frame every interactable region on the overlay (cells, cards, buttons, slots).</summary>
    public bool ShowRegions { get; set; } = true;
    /// <summary>Show OCR's text regions and the lines it read on the overlay.</summary>
    public bool ShowTextBoxes { get; set; } = true;
    /// <summary>Threads ONNX Runtime may use for OCR.</summary>
    public int OcrThreads { get; set; } = 4;
    /// <summary>Save a dataset sample on its own whenever the Sandbox's reading changes.</summary>
    public bool AutoCollect { get; set; } = true;
    /// <summary>At most one automatic sample per this many seconds.</summary>
    public double AutoCollectSeconds { get; set; } = 2.0;
    /// <summary>Move hints: the scanned position searched (Monte Carlo over the rules) and the best moves shown.</summary>
    public bool Hints { get; set; } = true;
    /// <summary>The overlay shows only the hint (its frames and summary), not what was read on the screen.</summary>
    public bool HintsOnly { get; set; }
    /// <summary>Hint quality: playouts per action (models/solver/levels.txt says what each level costs and gives).</summary>
    public int HintPlayouts { get; set; } = 1024;
    /// <summary>The most a hint may take, whatever its playouts (a slow PC, a position with many moves).</summary>
    public double HintSeconds { get; set; } = 0.5;
    /// <summary>
    /// What a Warden's Cache for an Inmate left at the Riot is worth in Chromatics, by rarity: Normal, Magic, Rare,
    /// Legendary, Ultimate. The hints weigh a sure cache against a chance at a Chromatic with it.
    /// </summary>
    public double[] CacheWorth { get; set; } = [.. Tartarus.Rules.RunScore.DefaultWorth];
    /// <summary>The game's language for OCR (en, ru, de, ..., zhCN); null: read from the game's GameUserSettings.ini.</summary>
    public string? GameLanguage { get; set; }
    /// <summary>The game's UE_game.log; null: looked for under the usual Steam libraries.</summary>
    public string? GameLogPath { get; set; }
    /// <summary>
    /// Hotkeys by action (scan, overlay, edit, save, auto, report), as "Ctrl+Shift+S"-style gestures; they work while the
    /// game or this app is in front.
    /// </summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new()
    {
        ["scan"] = "Ctrl+Shift+S",
        ["overlay"] = "Ctrl+Shift+H",
        ["edit"] = "Ctrl+Shift+E",
        ["save"] = "Ctrl+Shift+D",
        ["auto"] = "Ctrl+Shift+R",
        ["report"] = "Ctrl+Shift+B",
    };
    /// <summary>The defaults this file was last brought up to (see <see cref="Upgrade"/>); 0 in files older than it.</summary>
    public int SettingsVersion { get; set; }
    private const int CurrentVersion = 2;
    /// <summary>Where bug reports go (a Sentry DSN); empty: the app's own (BugReports.DefaultDsn).</summary>
    public string? ReportDsn { get; set; }
    /// <summary>A random id of this install: tells one tester's reports from another's, and nothing else about them.</summary>
    public string InstallId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public double? ControlLeft { get; set; }
    public double? ControlTop { get; set; }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TartarusSolver");
    public static string LogPath => Path.Combine(Folder, "app.log");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    // Relaxed escaping: "Ctrl+Shift+S" and paths stay readable for editing by hand.
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // In the repository when running from the source tree, else in LocalAppData.
    private static string DefaultDatasetDir() => Path.Combine(AppFiles.Root ?? Folder, "dataset");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) is { } settings)
            {
                settings.Upgrade();
                return settings;
            }
        }
        catch { }
        return new() { SettingsVersion = CurrentVersion };
    }

    /// <summary>Old defaults the player never changed become today's; hotkeys added since the file was written are added.</summary>
    private void Upgrade()
    {
        var defaults = new AppSettings();
        if (SettingsVersion < 2)
        {
            // Ctrl+Alt hotkeys became Ctrl+Shift; the scan interval went from 1 s to 0.5 s.
            foreach (var action in Hotkeys.Keys.ToList())
                if (Hotkeys[action].StartsWith("Ctrl+Alt+", StringComparison.OrdinalIgnoreCase) && defaults.Hotkeys.TryGetValue(action, out var now)
                    && string.Equals(Hotkeys[action][^1..], now[^1..], StringComparison.OrdinalIgnoreCase))
                    Hotkeys[action] = now;
            if (AutoScanSeconds == 1.0) AutoScanSeconds = defaults.AutoScanSeconds;
        }
        foreach (var (action, gesture) in defaults.Hotkeys) Hotkeys.TryAdd(action, gesture);
        SettingsVersion = CurrentVersion;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch { }
    }
}
