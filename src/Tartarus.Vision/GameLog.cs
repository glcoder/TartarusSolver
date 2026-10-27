using System.Globalization;
using System.Text.RegularExpressions;

namespace Tartarus.Vision;

/// <summary>A Tartarus run as the game's log tells it.</summary>
public sealed class LoggedRun
{
    public DateTime Start { get; set; }
    /// <summary>The last day the log saw, and the run's length (20; Iron Law 30).</summary>
    public int Day { get; set; }
    public int MaxDays { get; set; }
    /// <summary>Inmates that reached Chromatic, by their sounds as they reach it (one each).</summary>
    public int Chromatic { get; set; }
    public int Incidents { get; set; }
    /// <summary>Consume bites, chains' included.</summary>
    public int Consumes { get; set; }
    /// <summary>Consumes when the current day began.</summary>
    public int DayStartConsumes { get; set; }
    public bool ConsumedToday => Consumes > DayStartConsumes;
    public int InstrumentPicks { get; set; }
    public int Treasures { get; set; }
    public bool ObjectiveDone { get; set; }
    /// <summary>The Riot's result screen came up.</summary>
    public bool Finished { get; set; }
    /// <summary>When the Riot's result came up (the log's clock, as <see cref="Start"/>); null before.</summary>
    public DateTime? End { get; set; }
    /// <summary>The run's start is in the log (the Start button, or its day 0): its counts are the whole run's.</summary>
    public bool FromStart { get; set; }
    /// <summary>Instrument Re-rolls clicked.</summary>
    public int RerollsUsed { get; set; }
    /// <summary>The Consume target pick (the red screen) is open: from its sound until Cancel or the day's end.</summary>
    public bool ConsumePick { get; set; }
    /// <summary>When the Sandbox last changed as far as the log tells (a bite, a Chromatic, a Prison Incident), local time.</summary>
    public DateTime BoardChangedAt { get; set; }

    public int RemainingDays => MaxDays - Day;
    /// <summary>A pick (Torture Instrument, Warden's Treasure) is open, if out of sight while the Sandbox is viewed: its Confirm not clicked yet.</summary>
    public bool PickOpen { get; set; }
    /// <summary>The open pick is a Warden's Treasure (a Chromatic brought it).</summary>
    public bool TreasureOpen { get; set; }
    /// <summary>
    /// The Chromatics the log vouches for: their sounds, their panel slots lit, or the Warden's Treasures they brought,
    /// one each (the open one counted), whichever is most; at most 10.
    /// </summary>
    public int ChromaticAtLeast => Math.Min(10, Math.Max(Math.Max(Chromatic, PanelLights), Treasures + (TreasureOpen ? 1 : 0)));
    /// <summary>The Chromatic panel's slots lit up (an animation the log names): one per Chromatic.</summary>
    public int PanelLights { get; set; }
    /// <summary>
    /// Told by the pages the game opens (some clients' logs have no sounds, clicks or day counter): days counted by the
    /// new-day popup, the run's length known only without a theme (20), no Consumes, Re-rolls or Objective.
    /// </summary>
    public bool Paged { get; set; }
    /// <summary>The theme's scroll came up at the run's start (a log of pages doesn't say which theme).</summary>
    public bool ThemeShown { get; set; }
}

/// <summary>
/// The game's own log (UE_game.log): it says nothing of the Sandbox's contents, but it does mark the day
/// ("[S16LastDay] check cur=N max=M"), each Chromatic, Prison Incident, Consume, instrument pick and Warden's
/// Treasure, and the run's end. Read as a file the game writes; nothing of the game's memory or input is touched.
/// Each event counts once: the engine's "requested row ... not in DataTable" warning that follows a sound is skipped.
/// Some clients log none of that, only the pages they open: then the run is told from those (<see cref="LoggedRun.Paged"/>).
/// </summary>
public sealed partial class GameLog
{
    public List<LoggedRun> Runs { get; } = [];
    public LoggedRun? Current => Runs.Count > 0 ? Runs[^1] : null;

    private const string LogInGame = @"UE_game\TorchLight\Saved\Logs\UE_game.log";

    /// <summary>
    /// The game's log: the one set in settings; else the one beside the running game's executable
    /// (<paramref name="gameExe"/>, "...\UE_game\Binaries\Win64\torchlight_infinite.exe"); else the most recently
    /// written among the installs that Steam's library list, the Torchlight launcher's settings and the usual Steam
    /// folders on each drive name. Null when there's none.
    /// </summary>
    public static string? FindPath(string? configured = null, string? gameExe = null)
    {
        if (configured != null && File.Exists(configured)) return configured;
        if (gameExe != null && BesideGame(gameExe) is { } running) return running;
        var found = new List<FileInfo>();
        foreach (var root in InstallRoots())
        {
            try
            {
                var file = new FileInfo(Path.Combine(root, LogInGame));
                if (file.Exists && !found.Any(f => string.Equals(f.FullName, file.FullName, StringComparison.OrdinalIgnoreCase))) found.Add(file);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return found.OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }

    /// <summary>The log of the install that <paramref name="gameExe"/> belongs to: up its folders to the one holding UE_game.</summary>
    public static string? BesideGame(string gameExe)
    {
        for (var dir = new FileInfo(gameExe).Directory; dir != null; dir = dir.Parent)
        {
            string file = Path.Combine(dir.FullName, LogInGame);
            if (File.Exists(file)) return file;
        }
        return null;
    }

    /// <summary>Folders the game may be installed in ("...\Torchlight Infinite", holding UE_game).</summary>
    private static IEnumerable<string> InstallRoots()
    {
        const string steamGame = @"steamapps\common\Torchlight Infinite";
        var steams = new List<string>();
        if (OperatingSystem.IsWindows() && Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steamPath)
            steams.Add(steamPath.Replace('/', '\\'));
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            foreach (var library in new[] { "SteamLibrary", @"Program Files (x86)\Steam", "Steam", @"Games\Steam" })
                steams.Add(Path.Combine(drive.RootDirectory.FullName, library));
        }
        foreach (var steam in steams)
        {
            yield return Path.Combine(steam, steamGame);
            // Steam's list of its libraries: "path"		"G:\\SteamLibrary".
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            string text;
            try { text = File.ReadAllText(vdf); }
            catch (IOException) { continue; }
            foreach (Match m in VdfPathRx().Matches(text)) yield return Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), steamGame);
        }
        // The Torchlight launcher (no Steam): GameInstallationPaths=(("ReleaseGlobal", "G:/Games/TLI")).
        string launcher = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TorchlightLauncherNG");
        if (Directory.Exists(launcher))
        {
            foreach (var ini in Directory.EnumerateFiles(launcher, "TorchlightLauncherPreferences.ini", SearchOption.AllDirectories))
            {
                string text;
                try { text = File.ReadAllText(ini); }
                catch (IOException) { continue; }
                foreach (Match m in LauncherPathRx().Matches(text)) yield return m.Groups[1].Value.Replace('/', '\\');
            }
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPathRx();

    [GeneratedRegex("\"([A-Za-z]:[/\\\\][^\"\\r\\n]*)\"")]
    private static partial Regex LauncherPathRx();

    /// <summary>
    /// The game's language (its code: en, ru, de, ..., zhCN): LanguageCode in GameUserSettings.ini, found from the log's
    /// own path (Saved\Logs\UE_game.log, so Saved\Config\WindowsNoEditor); null when it isn't there.
    /// </summary>
    public static string? LanguageOf(string logPath)
    {
        try
        {
            string? saved = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(logPath)));
            if (saved == null) return null;
            string ini = Path.Combine(saved, "Config", "WindowsNoEditor", "GameUserSettings.ini");
            if (!File.Exists(ini)) return null;
            foreach (var line in File.ReadLines(ini))
                if (line.StartsWith("LanguageCode=", StringComparison.OrdinalIgnoreCase)) return line["LanguageCode=".Length..].Trim() is { Length: > 0 } code ? code : null;
        }
        catch (IOException) { }
        return null;
    }

    /// <summary>Takes one line of the log. Returns true when it changed what's known of the current run.</summary>
    public bool Feed(string line)
    {
        if (!line.Contains("S16", StringComparison.Ordinal)) return false;
        if (line.Contains("requested row", StringComparison.Ordinal)) return false;
        // Another client's log: the game's pages, no sounds, clicks or day counter. Not where the counter shows.
        if (line.Contains("Page@ ", StringComparison.Ordinal)) return !_dayCounter && FeedPage(line);
        // A new run: its Start button (then the objective, the Sandbox, day 0 and the first instrument pick).
        if (line.Contains("normal-click Btn_S16GamePlay_Start", StringComparison.Ordinal))
        {
            Runs.Add(new LoggedRun { Start = Time(line) ?? DateTime.Now, FromStart = true });
            return true;
        }
        if (DayRx().Match(line) is { Success: true } m)
        {
            int day = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), max = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            _dayCounter = true;
            // "max=0": the lobby's check, no run.
            if (max == 0) return false;
            // A run the log didn't see start (the log begins in it, a saved run went on), or the day count going back
            // without a Start button; day 0 is a start all the same.
            if (Current == null || day < Current.Day)
                Runs.Add(new LoggedRun { Start = Time(line) ?? DateTime.Now, FromStart = day == 0 });
            if (day > Current!.Day) (Current.DayStartConsumes, Current.ConsumePick) = (Current.Consumes, false);
            Current.Day = Math.Max(Current.Day, day);
            Current.MaxDays = max;
            return true;
        }
        var run = Current;
        if (run == null) return false;
        if (line.Contains("AudioEnum  == UI_S16_Eating_red_screen", StringComparison.Ordinal)) run.ConsumePick = true;
        else if (line.Contains("AudioEnum  == UI_S16_Click_Cancel", StringComparison.Ordinal) || line.Contains("] S16BoardNewDayPop Run", StringComparison.Ordinal)) run.ConsumePick = false;
        else if (line.Contains("normal-click Btn_S16Board_RelicPop_Reroll", StringComparison.Ordinal)) run.RerollsUsed++;
        // A Chromatic: its sound as it reaches the rarity, one each (the flight to the panel after it plays once for two
        // that come at once), and its panel slot lighting up. The flight's sound is no change of its own: the cell is
        // often empty by then, and a reading after it like those before looked like the old Sandbox still on screen
        // (2026-10-28: the eye button asked for again and again over a Warden's Treasure).
        else if (line.Contains("AudioEnum  == UI_S16_Premium_Quality_Fly", StringComparison.Ordinal)) { }
        else if (line.Contains("AudioEnum  == UI_S16_Premium_Quality", StringComparison.Ordinal)) (run.Chromatic, run.BoardChangedAt) = (run.Chromatic + 1, LocalTime(line));
        else if (line.Contains("Anim_Archive_LightUp", StringComparison.Ordinal)) run.PanelLights++;
        else if (line.Contains("] S16BoardEventPop Run", StringComparison.Ordinal)) (run.Incidents, run.BoardChangedAt) = (run.Incidents + 1, LocalTime(line));
        // A bite closes what the red sound opened as far as the log can tell (a chain's later picks have no sound of their own).
        else if (line.Contains("AudioEnum  == UI_S16_Own_Eat", StringComparison.Ordinal)) (run.Consumes, run.ConsumePick, run.BoardChangedAt) = (run.Consumes + 1, false, LocalTime(line));
        else if (line.Contains("] S16BoardRelicPop Run", StringComparison.Ordinal)) (run.InstrumentPicks, run.PickOpen) = (run.InstrumentPicks + 1, true);
        else if (line.Contains("] S16BoardColoredPop Run", StringComparison.Ordinal)) (run.PickOpen, run.TreasureOpen) = (true, true);
        else if (line.Contains("normal-click Btn_S16Board_RelicPop_Confirm", StringComparison.Ordinal)) run.PickOpen = false;
        // A Warden's Treasure taken works on the Sandbox at once (opens cells, raises rarities): a second one that comes
        // right after (two Chromatics at once) is picked over another Sandbox than the first.
        else if (line.Contains("normal-click Btn_S16Board_ColoredPop_Confirm", StringComparison.Ordinal)) (run.Treasures, run.PickOpen, run.TreasureOpen, run.BoardChangedAt) = (run.Treasures + 1, false, false, LocalTime(line));
        else if (line.Contains("AudioEnum  == UI_S16_Prison_Task_Done", StringComparison.Ordinal)) run.ObjectiveDone = true;
        // The result screen; it may come up twice for one run.
        else if (line.Contains("] S16GamePlayCheckOut Run", StringComparison.Ordinal)) (run.End, run.Finished) = (run.End ?? Time(line), true);
        else return false;
        return true;
    }

    // The log has the day counter (and the sounds and clicks with it): its pages aren't needed.
    private bool _dayCounter;
    // Pages: the entry screen or a run's result was the last seen (true), a run's page (false), nothing yet (null).
    private bool? _pageLobby;

    /// <summary>
    /// A page the game opens or closes ("Page@ OpenView S16BoardNewDayPop", "Page@ CloseView 3535_S16BoardColoredPopCtrl").
    /// A run begins with its first page after the entry screen or the last run's result (the theme's scroll, the
    /// Objective, the first instrument pick); each new-day popup is a day, each Chromatic brings a Warden's Treasure
    /// pick (one popup each), and the result screen ends the run.
    /// </summary>
    private bool FeedPage(string line)
    {
        bool open = line.Contains("Page@ OpenView ", StringComparison.Ordinal);
        if (!open && !line.Contains("Page@ CloseView ", StringComparison.Ordinal)) return false;
        bool Is(string page) => line.Contains(page, StringComparison.Ordinal);
        if (open && (Is("S16GamePlayEntry") || Is("S16GamePlayCheckOut")))
        {
            _pageLobby = true;
            if (!Is("S16GamePlayCheckOut") || Current is not { Paged: true, Finished: false } ended) return false;
            (ended.End, ended.Finished) = (Time(line), true);
            return true;
        }
        bool theme = Is("S16BoardThemePop");
        if (open && (theme || Is("S16BoardTaskPop") || Is("S16BoardRelicPop") || Is("S16BoardNewDayPop") || Is("S16BoardEventPop") || Is("S16BoardColoredPop"))
            && (Current is not { Paged: true } || _pageLobby != false))
        {
            // Seen from the entry screen on: the whole run, 20 days unless a theme (Iron Law: 30) says otherwise.
            bool start = _pageLobby == true;
            Runs.Add(new LoggedRun { Start = Time(line) ?? DateTime.Now, Paged = true, FromStart = start, MaxDays = start && !theme ? 20 : 0 });
            _pageLobby = false;
        }
        if (Current is not { Paged: true } run) return false;
        if (open)
        {
            if (theme) run.ThemeShown = true;
            else if (Is("S16BoardNewDayPop")) (run.Day, run.DayStartConsumes, run.ConsumePick) = (run.Day + 1, run.Consumes, false);
            else if (Is("S16BoardEventPop")) (run.Incidents, run.BoardChangedAt) = (run.Incidents + 1, LocalTime(line));
            else if (Is("S16BoardRelicPop")) (run.InstrumentPicks, run.PickOpen) = (run.InstrumentPicks + 1, true);
            else if (Is("S16BoardColoredPop")) (run.Chromatic, run.PickOpen, run.TreasureOpen, run.BoardChangedAt) = (run.Chromatic + 1, true, true, LocalTime(line));
            else return false;
        }
        else if (Is("_S16BoardRelicPopCtrl")) run.PickOpen = false;
        else if (Is("_S16BoardColoredPopCtrl")) (run.Treasures, run.PickOpen, run.TreasureOpen) = (run.Treasures + 1, false, false);
        else return false;
        return true;
    }

    public static GameLog Read(string path)
    {
        var log = new GameLog();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) log.Feed(line);
        return log;
    }

    /// <summary>A line's time (the log writes UTC) in local time; now when it doesn't read.</summary>
    /// <summary>When the line was written, to the millisecond (a Sandbox change is told against frames' times), in local time.</summary>
    private static DateTime LocalTime(string line) =>
        (line.Length > 24 && DateTime.TryParseExact(line[1..24], "yyyy.MM.dd-HH.mm.ss:fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ms) ? ms : Time(line)) is { } t
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime() : DateTime.Now;

    private static DateTime? Time(string line) =>
        line.Length > 24 && DateTime.TryParseExact(line[1..20], "yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    [GeneratedRegex(@"\[S16LastDay\] check cur=(\d+) max=(\d+)")]
    private static partial Regex DayRx();
}

/// <summary>Follows the game's log as the game writes it (new lines every half second), from its current end.</summary>
public sealed class GameLogWatcher : IDisposable
{
    private readonly string _path;
    private readonly Timer _timer;
    private readonly object _polling = new();
    private readonly System.Text.Decoder _decoder = System.Text.Encoding.UTF8.GetDecoder();
    private long _position;
    private string _partial = "";

    /// <summary>Reads the log from its start (the run going on, even one begun before the app), then follows it.</summary>
    public GameLogWatcher(string path, GameLog? log = null)
    {
        _path = path;
        Log = log ?? new GameLog();
        _timer = new Timer(_ => Poll(), null, 0, 500);
    }

    public GameLog Log { get; }
    /// <summary>Raised (on a timer thread) when the current run changed.</summary>
    public event Action<LoggedRun>? Changed;

    private void Poll()
    {
        // The first read of a long log may outlast the timer's period.
        if (!Monitor.TryEnter(_polling)) return;
        try
        {
            var info = new FileInfo(_path);
            if (info.Length < _position) (_position, _partial) = (0, ""); // a new log
            if (info.Length == _position) return;
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_position, SeekOrigin.Begin);
            var bytes = new byte[1 << 20];
            var chars = new char[System.Text.Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            bool changed = false;
            int n;
            while ((n = stream.Read(bytes, 0, bytes.Length)) > 0)
            {
                _position += n;
                string text = _partial + new string(chars, 0, _decoder.GetChars(bytes, 0, n, chars, 0));
                int end = text.LastIndexOf('\n');
                _partial = end < 0 ? text : text[(end + 1)..];
                if (end < 0) continue;
                lock (Log)
                    foreach (var line in text[..end].Split('\n'))
                        changed |= Log.Feed(line);
            }
            if (changed && Log.Current is { } run) Changed?.Invoke(run);
        }
        catch (IOException)
        {
        }
        finally
        {
            Monitor.Exit(_polling);
        }
    }

    public void Dispose() => _timer.Dispose();
}
