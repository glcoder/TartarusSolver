using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Tartarus.Capture;
using Tartarus.Vision;
using Frame = Tartarus.Vision.Frame;
using Tartarus.App.Localization;

namespace Tartarus.App;

/// <summary>
/// The small control window: picks and captures the game window, runs scans, drives the overlay, and
/// turns corrected scans into dataset samples. Everything here runs on the UI thread; scans run on
/// <see cref="ScanService"/>'s thread and frames arrive on capture threads.
/// </summary>
public partial class ControlWindow : Window, IHintSink
{
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay = new();
    private readonly ScanService _scans;
    private readonly DispatcherTimer _placeTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    // The auto scan loop running (a new one bumps it: the old one stops).
    private int _autoScanLoop;
    private readonly DispatcherTimer _attachTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private HotkeyManager? _hotkeys;
    private Dataset _dataset;

    private IGameCapture? _capture;
    // A capture being set up off the UI thread: no second one meanwhile.
    private bool _startingCapture;
    private bool _findingGame;
    private WindowInfo? _target;

    // What the overlay shows: the last scanned frame, its scan, and its labels (edited by hand in edit mode).
    private Frame? _frame;
    // Frames' buffers, given back once a frame is no one's (it went to the report history as a small picture).
    private readonly PixelPool _pixels = new();
    private ScanResult? _scan;
    private EditableSandbox? _scannedSandbox;
    // The last scan as read, before KeepCovered and Steady: what Steady compares the next one with.
    private EditableSandbox? _lastRead;
    // The last scan's reading as the hints settle on it: tooltips filled in, not steadied (the Consume target pick's votes).
    private EditableSandbox? _settleRead;
    private EditableSandbox? _sandbox;
    private int _selected = -1;
    private bool _editMode;
    private bool _savedSinceScan;
    private string? _lastAutoDescription;
    private string? _status;

    public ControlWindow(AppSettings settings)
    {
        _settings = settings;
        // A quarter of the cores at most (one at least): a 4-core PC keeps three for the game.
        _scans = new ScanService(Math.Clamp(Environment.ProcessorCount / 4, 1, Math.Max(1, settings.OcrThreads)));
        _hints = new HintService(settings.HintPlayouts, settings.HintSeconds);
        _hints.Log += line => Dispatcher.BeginInvoke(() => Log("Hint: " + line));
        ContentRendered += (_, _) => _hints.WarmUp(error => AppLog.Write("Hint warm-up failed: " + error));
        _hints.Ready += hint => Dispatcher.BeginInvoke(() =>
        {
            // As the overlay ranks them (on the Consume target pick, Cancel with the targets).
            var ranked = hint.Ranked;
            var best = ranked.Count > 0 ? ranked[0] : null;
            Tartarus.Rules.GameAction? before = _hint?.Ranked is { Count: > 0 } prev ? prev[0].Action : null;
            if (best != null && before != best.Action)
                Log($"Hint: {SandboxOverlayView.RankedName(hint, best)} worth ≈{best.Mean:0.000} ({best.Chromatic:0.00} Chromatic)"
                    + string.Concat(ranked.Skip(1).Where(t => best.Mean - t.Mean < Hint.TieWithin).Select(t => $", tied with {SandboxOverlayView.RankedName(hint, t)} ≈{t.Mean:0.000}"))
                    + $" ({hint.Result.Elapsed.TotalMilliseconds:0} ms)");
            _hint = hint;
            _hintShownAt = DateTime.Now;
            HintStatus.Text = Loc.F(Strings.Main_LastHint,
                    hint.Result.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                    hint.Result.Playouts.ToString("N0", CultureInfo.InvariantCulture))
                + (_hints.Net != null ? Strings.Main_ValueNet : Strings.Main_NoValueNet) + (_hints.Policy != null ? Strings.Main_PolicyNet : "");
            RefreshOverlay();
        });
        _dataset = new Dataset(settings.DatasetDir);
        Directory.CreateDirectory(AppSettings.Folder);
        _flow = new HintFlow(this);
        InitializeComponent();
        StartGameLog();
        SetupHintSlider();
        // Only where some screen shows its title bar (2026-11-10: saved 657 px above the screen, a monitor gone: the window never seen).
        if (settings.ControlLeft is { } left && settings.ControlTop is { } top
            && new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight).Contains(left + 100, top + 15))
        {
            Left = left;
            Top = top;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        string Hk(string action) => settings.Hotkeys.TryGetValue(action, out var g) && !string.IsNullOrWhiteSpace(g) ? $" ({g})" : "";
        ScanButton.Content = Strings.Main_Scan + Hk("scan");
        OverlayCheck.Content = Strings.Main_Overlay + Hk("overlay");
        EditCheck.Content = Strings.Main_EditMode + Hk("edit");
        SaveButton.Content = Strings.Main_SaveFrame + Hk("save");
        AutoScanCheck.Content = Strings.Main_Auto + Hk("auto") + Strings.Main_Every;
        ReportButton.Content = Strings.Main_ReportBug + Hk("report");
        Title = $"Tartarus Solver {AppVersion.Number}" + (settings.Debug ? Strings.Main_Debug : "");
        // The player's window: the game window, scanning, the hints' quality, the game's log, the version
        // and bug reports.
        if (!settings.Debug)
            foreach (var panel in new FrameworkElement[] { ViewPanel, HintsCheck, HintsOnlyCheck, DebugPanel }) panel.Visibility = Visibility.Collapsed;
        VersionText.Text = Strings.Main_Version + " " + AppVersion.Short;
        VersionText.ToolTip = AppVersion.Full;
        Log("Version " + AppVersion.Short + (BugReports.Enabled ? "" : " · bug reports not set up"));

        settings.AutoScanSeconds = Math.Clamp(Math.Round(settings.AutoScanSeconds, 1), 0, 1);
        IntervalSlider.Value = settings.AutoScanSeconds;
        ShowInterval();
        AutoScanCheck.IsChecked = settings.AutoScan;
        OverlayCheck.IsChecked = settings.OverlayVisible;
        BoxesCheck.IsChecked = settings.ShowRegions;
        TextCheck.IsChecked = settings.ShowTextBoxes;
        AutoCollectCheck.IsChecked = settings.AutoCollect;
        GameLanguageCombo.Items.Add(new ComboBoxItem { Content = Strings.Common_Automatic, Tag = "auto" });
        foreach (var code in Tartarus.Vision.Ocr.OcrPacks.GameLanguages)
        {
            string name = Tartarus.Vision.Ocr.OcrPacks.LanguageName(code), native = Tartarus.Vision.Ocr.OcrPacks.NativeName(code);
            GameLanguageCombo.Items.Add(new ComboBoxItem { Content = name == native ? name : $"{name} · {native}", Tag = code });
        }
        ShowGameLanguage(ManualLanguage ?? "en");
        GameLanguageCombo.SelectionChanged += GameLanguageCombo_SelectionChanged;
        // The interface's own language, each by its own name; automatic is Windows's. The XAML's words are swapped when a
        // window is built, so a change takes a restart.
        UiLanguageCombo.Items.Add(new ComboBoxItem { Content = Strings.Common_Automatic, Tag = "auto" });
        foreach (var code in Tartarus.Vision.Ocr.OcrPacks.GameLanguages)
            UiLanguageCombo.Items.Add(new ComboBoxItem { Content = Tartarus.Vision.Ocr.OcrPacks.NativeName(code), Tag = code });
        bool automatic = string.IsNullOrWhiteSpace(settings.UiLanguage) || settings.UiLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase);
        UiLanguageCombo.SelectedItem = UiLanguageCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i =>
            automatic ? (string)i.Tag == "auto" : (string)i.Tag != "auto" && Loc.Resolve((string)i.Tag) == Loc.Resolve(settings.UiLanguage)) ?? UiLanguageCombo.Items[0];
        UiLanguageCombo.SelectionChanged += UiLanguageCombo_SelectionChanged;
        _placeTimer.Tick += (_, _) => PlaceOverlay();
        _attachTimer.Tick += (_, _) => TryAutoAttach();

        _overlay.View.MouseLeftButtonUp += Overlay_MouseUp;
        _overlay.View.MouseRightButtonUp += Overlay_MouseUp;

        RefreshWindowList();
        UpdateDatasetStatus();
        SetupUpdates();
        ShowStats();
        // Last: every line the XAML wrote, in the interface's language where the table has a word for it.
        Loc.Apply(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        new WindowInteropHelper(_overlay).EnsureHandle();
        _hotkeys = new HotkeyManager(HwndSource.FromHwnd(new WindowInteropHelper(this).Handle));
        var failed = new List<string>();
        void Hotkey(string action, Action run)
        {
            if (!_settings.Hotkeys.TryGetValue(action, out var gesture) || string.IsNullOrWhiteSpace(gesture)) return;
            if (!_hotkeys.Register(gesture, run)) failed.Add($"{gesture} ({action})");
        }
        Hotkey("scan", () => _ = ScanAsync(manual: true));
        Hotkey("overlay", () => OverlayCheck.IsChecked = OverlayCheck.IsChecked != true);
        if (_settings.Debug)
        {
            Hotkey("edit", () => EditCheck.IsChecked = EditCheck.IsChecked != true);
            Hotkey("save", () => _ = SaveSampleAsync());
        }
        Hotkey("auto", () => AutoScanCheck.IsChecked = AutoScanCheck.IsChecked != true);
        Hotkey("report", () => Report());
        if (failed.Count > 0)
            Log($"Hotkeys not registered (taken by another app or invalid): {string.Join(", ", failed)}. Change them in {Path.Combine(AppSettings.Folder, "settings.json")}");

        _placeTimer.Start();
        _attachTimer.Start();
        TryAutoAttach();
        _ = WarmUpAsync();
    }

    private async Task WarmUpAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await _scans.WarmUpAsync();
            Log($"Engine ready in {sw.ElapsedMilliseconds} ms: {_scans.ScannerInfo}");
        }
        catch (Exception ex)
        {
            Log("Scanner failed to start: " + ex.Message);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _logSearch++;
        _settings.ControlLeft = Left;
        _settings.ControlTop = Top;
        _settings.Save();
        _placeTimer.Stop();
        _autoScanLoop++;
        _attachTimer.Stop();
        _hotkeys?.Dispose();
        StopCapture();
        _overlay.Close();
        // Off the UI thread: freeing a big recognizer took over 5 s on closing (2026-11-01, Sentry: the Chinese one), and
        // the app goes away anyway.
        Task.Run(_scans.Dispose);
        _hints.Dispose();
        _gameLog?.Dispose();
        base.OnClosed(e);
    }

    // ---------- Target window and capture ----------

    private void RefreshWindowList()
    {
        var windows = Win32Windows.List();
        var previous = WindowCombo.SelectedItem as WindowInfo?;
        WindowCombo.ItemsSource = windows;
        var pattern = new Regex(_settings.WindowTitle);
        WindowCombo.SelectedItem = windows.FirstOrDefault(w => previous is { } p && w.Hwnd == p.Hwnd) is { Hwnd: not 0 } same ? same
            : Win32Windows.FindGame(windows, pattern);
    }

    private string? _gameLanguage;
    // Asking for (or downloading) a language's recognizer: the overlay stands aside, the check waits.
    private bool _askingOcr;
    // Recognizers the player said no to this session: not asked again until the next start.
    private readonly HashSet<string> _declinedOcr = [];
    // A recognizer whose download failed is offered once more, a few minutes later: the network may be back.
    private readonly HashSet<string> _reofferedOcr = [];
    private DateTime? _reofferOcrAt;
    private static readonly TimeSpan ReofferOcrAfter = TimeSpan.FromMinutes(3);

    // The list showing a language, not the player choosing one.
    private bool _showingLanguage;

    /// <summary>The language to read by the player's choice; null: the game's settings file says.</summary>
    private string? ManualLanguage => !_settings.DetectGameLanguage && !string.IsNullOrEmpty(_settings.GameLanguage) ? _settings.GameLanguage : null;

    // The language in the game's settings file beside _iniPath (the log it was read for).
    private string? _iniLanguage, _iniPath;
    private bool _readingIni;

    /// <summary>
    /// Reads the game's settings file off the UI thread (2026-11-01, a hang report: opening a file on the game's drive
    /// stood the window still for over 5 s), then checks the language again.
    /// </summary>
    private async void ReadGameIni()
    {
        if (_readingIni || ManualLanguage != null || _gameLogPath is not { } path) return;
        _readingIni = true;
        try
        {
            _iniLanguage = await Task.Run(() =>
            {
                try { return GameLog.LanguageOf(path); }
                catch (Exception) { return null; }
            });
            _iniPath = path;
        }
        finally
        {
            _readingIni = false;
        }
        CheckGameLanguage();
    }

    /// <summary>
    /// The game's language (the player's choice, else the game's own settings file beside its log) to OCR: checked every
    /// few seconds, the player may switch it in the game. A language whose recognizer isn't installed is offered for
    /// download first.
    /// </summary>
    private void CheckGameLanguage()
    {
        if (_askingOcr) return;
        string? fromIni = null;
        if (ManualLanguage == null && _gameLogPath is { } log)
        {
            // This log's settings file not read yet: its language is waited for rather than English taken first.
            if (!string.Equals(_iniPath, log, StringComparison.OrdinalIgnoreCase))
            {
                ReadGameIni();
                return;
            }
            fromIni = _iniLanguage;
        }
        string code = ManualLanguage ?? fromIni ?? "en";
        // No language chosen yet: asked as the game connects, the one found marked (2026-11-02: many reports came from
        // clients whose settings file says "en" with the game in Chinese, and the text-unread prompt came late or never).
        if (string.IsNullOrEmpty(_settings.GameLanguage) && !_askedFirstLanguage && _capture != null && _target is { } target
            && Win32Windows.IsGame(target, new Regex(_settings.WindowTitle)))
        {
            _askedFirstLanguage = true;
            _askingOcr = true;
            Dispatcher.BeginInvoke(() => AskGameLanguage(code, first: true), DispatcherPriority.ApplicationIdle);
            return;
        }
        if (code == _gameLanguage && !(_reofferOcrAt is { } at && DateTime.Now >= at)) return;
        _reofferOcrAt = null;
        _gameLanguage = code;
        _textUnread = false;
        ShowGameLanguage(code);
        var pack = Tartarus.Vision.Ocr.OcrPacks.ForGame(code);
        // The program's own models gone from under it (2026-11-01: found at the start, not 6 s later, and the language
        // list threw at every choice): what's downloaded still counts, and the running engine reads on.
        string? modelDir = Tartarus.Vision.Ocr.OcrEngine.TryFindModelDir();
        if (modelDir == null) WarnModelsGone();
        bool have = Tartarus.Vision.Ocr.OcrPacks.Locate(pack, modelDir ?? Tartarus.Vision.Ocr.OcrPacks.DownloadDir) != null;
        Log($"Game language: {code}{(ManualLanguage != null ? " (set)" : "")} · OCR {(have ? pack.Id : $"{pack.Id} recognizer not installed")}");
        if (!have && modelDir == null) return;
        if (!have && !_declinedOcr.Contains(pack.Id))
        {
            // After the window is up: this runs from its start too.
            Dispatcher.BeginInvoke(() => OfferOcrDownload(pack, code), DispatcherPriority.ApplicationIdle);
            return;
        }
        _scans.SetGameLanguage(code);
    }

    private bool _warnedModelsGone;

    private void WarnModelsGone()
    {
        if (_warnedModelsGone) return;
        _warnedModelsGone = true;
        Log($"OCR models not found in {AppContext.BaseDirectory}");
        Dispatcher.BeginInvoke(() => MessageBox.Show(this, Strings.Main_FilesMissing,
            "Tartarus Solver", MessageBoxButton.OK, MessageBoxImage.Warning), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// The list's selection. Automatic: the game's settings file says, and the item shows what it said; else the player's
    /// choice, which stays in the settings either way (the settings file may not tell what the game shows: 2026-10-31, a
    /// TapTap client in Chinese with "en" in it).
    /// </summary>
    private void ShowGameLanguage(string code)
    {
        _showingLanguage = true;
        var items = GameLanguageCombo.Items.OfType<ComboBoxItem>().ToList();
        // The closed list keeps the text it had when selected: selected anew below.
        GameLanguageCombo.SelectedItem = null;
        items[0].Content = _settings.DetectGameLanguage
            ? $"{Strings.Common_Automatic} ({Tartarus.Vision.Ocr.OcrPacks.NativeName(code)})"
            : Strings.Common_Automatic;
        GameLanguageCombo.SelectedItem = _settings.DetectGameLanguage
            ? items[0]
            : items.Skip(1).FirstOrDefault(i => Tartarus.Vision.Ocr.OcrPacks.LanguageName((string)i.Tag) == Tartarus.Vision.Ocr.OcrPacks.LanguageName(code));
        _showingLanguage = false;
    }

    /// <summary>The interface's language chosen: every window is built in it, so the app starts again, asked in that language.</summary>
    private void UiLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UiLanguageCombo.SelectedItem is not ComboBoxItem { Tag: string code } || code == _settings.UiLanguage) return;
        _settings.UiLanguage = code;
        _settings.Save();
        if (Loc.Resolve(code) == Loc.Language) return;
        if (MessageBox.Show(this, Loc.In(code, nameof(Strings.Language_RestartQuestion)), "Tartarus Solver",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        // Once this one is down: the new one takes the hotkeys.
        if (Environment.ProcessPath is { } exe)
            Application.Current.Exit += (_, _) => System.Diagnostics.Process.Start(exe, Environment.GetCommandLineArgs().Skip(1));
        Application.Current.Shutdown();
    }

    private void GameLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showingLanguage || (GameLanguageCombo.SelectedItem as ComboBoxItem)?.Tag is not string code) return;
        if (code == "auto")
        {
            _settings.DetectGameLanguage = true;
            _settings.Save();
            CheckGameLanguage();
            ShowGameLanguage(_gameLanguage ?? "en");
            return;
        }
        _settings.GameLanguage = code;
        _settings.DetectGameLanguage = false;
        ShowGameLanguage(code);
        _settings.Save();
        // Chosen by hand: its recognizer is offered again, even if declined before.
        _declinedOcr.Remove(Tartarus.Vision.Ocr.OcrPacks.ForGame(code).Id);
        CheckGameLanguage();
    }

    // Sandbox frames in a row whose text didn't read in the game language taken (none read since the first of them).
    private int _unreadFrames;
    private DateTime _unreadSince;
    private string? _unreadLanguage;
    // The game's text doesn't read in the language taken, until a frame's does: the overlay says so.
    private bool _textUnread;
    // In the interface's language alone (the player's choice, else Windows's). Once in all 13: each a satellite assembly, loaded
    // at start (2026-11-06: start hangs of 31 s loading the 12 others, Windows 11 26200/26300).
    private static string LanguageAsk => Strings.Language_Title + "\n" + Strings.Language_Choose;
    // Languages the player was asked about this session: not asked again until the next start.
    private readonly HashSet<string> _askedLanguage = [];
    // No language chosen and asked this session: "Not now" asks again at the next start.
    private bool _askedFirstLanguage;

    /// <summary>
    /// The Objective panel's words read as no Objective's name on Sandbox after Sandbox for a while: the game shows
    /// another language than the one taken (one frame in 60 misreads mid-fade, a wrong language every frame), so the
    /// player is asked which one, once, and the overlay keeps asking while it lasts (2026-11-02: a third of those asked
    /// said "Not now", and played a run on hints that couldn't see the cards).
    /// </summary>
    private void CheckTextReadable(Tartarus.Vision.Ocr.ScreenText text, DateTime at)
    {
        if (_gameLanguage is not { } language || _askingOcr) return;
        // Its recognizer isn't there (declined, or still loading): unread text is expected.
        if (_scans.OcrPack != Tartarus.Vision.Ocr.OcrPacks.ForGame(language).Id) return;
        if (language != _unreadLanguage) { _unreadLanguage = language; _unreadFrames = 0; }
        switch (Tartarus.Vision.GameObservation.TextReadable(text))
        {
            case true: (_unreadFrames, _textUnread) = (0, false); return;
            case null: return;
        }
        if (_unreadFrames++ == 0) _unreadSince = at;
        if (_unreadFrames < 10 || at - _unreadSince < TimeSpan.FromSeconds(5)) return;
        _textUnread = true;
        if (!_askedLanguage.Add(language)) return;
        Log($"Game language: the game's text doesn't read as {language} ({_unreadFrames} Sandbox frames in {(at - _unreadSince).TotalSeconds:0} s), asking");
        Dispatcher.BeginInvoke(() => AskGameLanguage(language), DispatcherPriority.ApplicationIdle);
    }

    /// <param name="first"><paramref name="current"/> is the language found, not one that failed to read.</param>
    private void AskGameLanguage(string current, bool first = false)
    {
        _askingOcr = true;
        PlaceOverlay();
        string? chosen;
        try
        {
            var window = new GameLanguageWindow(current, first);
            if (IsVisible) window.Owner = this;
            chosen = window.ShowDialog() == true ? window.Chosen : null;
        }
        finally
        {
            _askingOcr = false;
            PlaceOverlay();
        }
        if (chosen == null)
        {
            Log("Game language: the player kept " + current);
            if (first) CheckGameLanguage();
            return;
        }
        Log("Game language: the player chose " + chosen);
        _settings.GameLanguage = chosen;
        // The language found, confirmed: detection goes on following the game.
        if (first && _settings.DetectGameLanguage && Tartarus.Vision.Ocr.OcrPacks.LanguageName(chosen) == Tartarus.Vision.Ocr.OcrPacks.LanguageName(current))
        {
            _settings.Save();
            CheckGameLanguage();
            return;
        }
        // Automatic goes off: the list shows the choice.
        _settings.DetectGameLanguage = false;
        ShowGameLanguage(chosen);
        _settings.Save();
        _declinedOcr.Remove(Tartarus.Vision.Ocr.OcrPacks.ForGame(chosen).Id);
        CheckGameLanguage();
    }

    private void OfferOcrDownload(Tartarus.Vision.Ocr.OcrPack pack, string code)
    {
        _askingOcr = true;
        PlaceOverlay();
        try
        {
            var window = new DownloadWindow(pack, code);
            if (IsVisible) window.Owner = this;
            if (window.ShowDialog() != true)
            {
                if (window.Failed && _reofferedOcr.Add(pack.Id)) _reofferOcrAt = DateTime.Now + ReofferOcrAfter;
                else _declinedOcr.Add(pack.Id);
            }
            Log(window.Result);
        }
        finally
        {
            _askingOcr = false;
            PlaceOverlay();
        }
        // Downloaded: the recognizer takes over from the next scan; declined: English reads the digits at least.
        _scans.SetGameLanguage(code);
    }

    // Windows won't capture a window on this PC: a copy of the screen instead, for the rest of this session. At once when it
    // has no such capture (2026-11-04, Windows 10 1809: a player restarted the app for an hour), after a few refusals when
    // it might have been a moment's (10 of 162 installs in reports never had a frame).
    private bool _screenCopy;
    private int _captureRefusals;
    private const int RefusalsForScreenCopy = 3;
    // Device lost again and again with no frame between (2026-11-07, an Arc B580: 884 new captures in 14 min, none gave a frame):
    // no use trying that way. A card's reset is over after one (7 of 7 in reports).
    private int _lostWithoutFrame;
    private const int LossesForScreenCopy = 3;
    private bool _copyCovered;

    // A capture that fails is tried again later and later, the failure said when it changes and at each tenth try (2026-11-03:
    // tried every 2 s for hours, 859 lines of the same failure in a log, and a Direct3D device left behind by each).
    private int _captureFails;
    private string? _captureFailure;
    private DateTime _captureNextTry;
    private static readonly TimeSpan CaptureRetryMax = TimeSpan.FromMinutes(1);

    private async void TryAutoAttach()
    {
        CheckGameLanguage();
        // The player may switch the language in the game: its settings file read again.
        ReadGameIni();
        if (_capture != null || _startingCapture || _findingGame) return;
        if (AutoAttachCheck.IsChecked != true)
        {
            CaptureStatus.Text = Strings.Status_NotCapturing;
            return;
        }
        if (DateTime.Now < _captureNextTry) return;
        var pattern = new Regex(_settings.WindowTitle);
        // Every window on the desktop is looked at: off the UI thread, so a slow one can't stand the window still.
        _findingGame = true;
        WindowInfo game;
        try { game = await Task.Run(() => Win32Windows.FindGame(pattern) ?? default); }
        finally { _findingGame = false; }
        if (_capture != null || AutoAttachCheck.IsChecked != true) return;
        // What it waits for, said (2026-11-01: players started the app with the game minimized or not yet running, saw
        // only "Not capturing" and sent bug reports).
        if (game.Hwnd == IntPtr.Zero)
        {
            // A new game window starts afresh, without the old one's pause.
            _captureFails = 0;
            CaptureStatus.Text = Strings.Status_WaitingForGame;
        }
        else if (Win32Windows.IsMinimized(game.Hwnd)) CaptureStatus.Text = Strings.Status_GameMinimized;
        else StartCapture(game);
    }

    /// <summary>Captures <paramref name="w"/>; <paramref name="keepOverlay"/>: the same window again, the overlay stays up.</summary>
    private async void StartCapture(WindowInfo w, bool keepOverlay = false)
    {
        if (_startingCapture) return;
        bool useScreenCopy = false;
        StopCapture(hideOverlay: !keepOverlay);
        _startingCapture = true;
        try
        {
            // Off the UI thread: on a Windows Insider build (28000) the capture item's Closed event never got subscribed
            // to from it, and the window stood still at every start (2026-10-30, a player's hang reports).
            bool byScreenCopy = _screenCopy || _settings.ScreenCopy;
            IGameCapture capture = await Task.Run<IGameCapture>(() => byScreenCopy ? new ScreenCopyCapture(w.Hwnd) : new WindowCapture(w.Hwnd));
            capture.Note += AppLog.Write;
            capture.Closed += () => Dispatcher.BeginInvoke(() =>
            {
                if (_capture != capture) return;
                Log($"Window \"{w.Title}\" closed");
                StopCapture();
            });
            _capture = capture;
            (_captureFails, _captureFailure, _captureNextTry) = (0, null, default);
            _target = w;
            CaptureButton.Content = Strings.Main_Stop;
            _copyCovered = false;
            CaptureStatus.Text = byScreenCopy ? Strings.Status_ScreenCopyOn : Loc.F(Strings.Status_Capturing, w);
            Log($"Capturing window \"{w.Title}\" ({w.Process}){(byScreenCopy ? " by screen copy" : "")}, {Win32Windows.VisibleBounds(w.Hwnd)}");
            if (capture is ScreenCopyCapture copy) LeaveOutOfCopies(copy);
            // The log of the very install that's running, from its executable's path.
            if (Win32Windows.ImagePath(w.Pid) is { } exe) StartGameLog(exe);
            CheckGameLanguage();
        }
        catch (Exception ex) when (!_screenCopy && (WindowCapture.IsUnsupported(ex) || WindowCapture.IsRefused(ex) && ++_captureRefusals >= RefusalsForScreenCopy))
        {
            Log($"Could not start capturing \"{w.Title}\": {ex.Message} A copy of the screen from now on.");
            _screenCopy = true;
            _captureNextTry = default;
            useScreenCopy = true;
        }
        catch (Exception ex)
        {
            _captureFails++;
            if (ex.Message != _captureFailure || _captureFails % 10 == 0)
                Log($"Could not start capturing \"{w.Title}\": {ex.Message} (try {_captureFails})");
            _captureFailure = ex.Message;
            double wait = Math.Min(CaptureRetryMax.TotalSeconds, Math.Pow(2, Math.Min(_captureFails, 10)));
            _captureNextTry = DateTime.Now.AddSeconds(wait);
            // 被 Windows 拒绝时用它自带的多语言提示（作者已备好简体中文），其余情况走我们的译文表。
            CaptureStatus.Text = WindowCapture.IsRefused(ex)
                ? $"{Strings.Status_CaptureRefused} (0x{ex.HResult:X8})"
                : Strings.Status_CaptureFailed;
        }
        finally
        {
            _startingCapture = false;
        }
        if (useScreenCopy) StartCapture(w, keepOverlay);
    }

    /// <summary>Stops capturing; <paramref name="hideOverlay"/> false when the same window is captured again at once.</summary>
    private void StopCapture(bool hideOverlay = true)
    {
        // Off the UI thread too: closing the capture once hung in Windows' code (2026-10-30, Sentry, an Insider build).
        if (_capture is ScreenCopyCapture) BackInCaptures();
        if (_capture is { } old) Task.Run(old.Dispose);
        _capture = null;
        _target = null;
        CaptureButton.Content = Strings.Main_Capture;
        CaptureStatus.Text = Strings.Status_NotCapturing;
        if (hideOverlay && _overlay.IsVisible) _overlay.Hide();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshWindowList();

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capture != null)
        {
            // Stopping by hand also stops re-attaching, or the game would be picked up again in 2 s.
            AutoAttachCheck.IsChecked = false;
            StopCapture();
            return;
        }
        if (WindowCombo.SelectedItem is WindowInfo w && Win32Windows.Exists(w.Hwnd)) StartCapture(w);
        else
        {
            RefreshWindowList();
            Log("Pick a window in the list");
        }
    }

    // ---------- Overlay placement ----------

    private void PlaceOverlay()
    {
        // The hotkeys are held only while the game or this app is in front: elsewhere the keys are other apps'.
        int foreground = Win32Windows.ProcessIdOf(Win32Windows.Foreground);
        if (_hotkeys != null) _hotkeys.Active = foreground == Environment.ProcessId || _target is { } game && foreground == game.Pid;
        // On top only over the game (or when this window is in front): over other work it's an ordinary window, not in the
        // way (2026-11-01, the user's ask). Set on a change only: each one has WPF wait on its render thread.
        bool overGame = foreground == Environment.ProcessId || _capture != null && _target is { } played && foreground == played.Pid;
        if (Topmost != overGame) Topmost = overGame;
        if (_capture == null || _target is not { } target) return;
        IntPtr hwnd = target.Hwnd;
        if (!Win32Windows.Exists(hwnd))
        {
            StopCapture();
            return;
        }
        bool front = !_settings.HideWhenGameInactive || foreground == target.Pid || foreground == Environment.ProcessId;
        // Not over the bug report's window: the overlay keeps itself on top of everything.
        bool show = OverlayCheck.IsChecked == true && !Win32Windows.IsMinimized(hwnd) && front && !_reporting && !_askingOcr;
        if (!show)
        {
            if (_overlay.IsVisible) _overlay.Hide();
            return;
        }
        // Frames are the client area (see WindowCapture.CropToClient); the overlay covers exactly that.
        var bounds = Win32Windows.ClientBounds(hwnd);
        if (bounds.IsEmpty || _capture.CropToClient == false) bounds = Win32Windows.VisibleBounds(hwnd);
        long placing = Environment.TickCount64;
        if (!_overlay.IsVisible) _overlay.Show();
        _overlay.PlaceOver(bounds, hwnd);
        // The UI waits on WPF's render thread here (2026-11-07: 4 of 6 hang reports of 0.7.46, at the first placement over the
        // game); how long it takes when it's under the watchdog's 5 s, said.
        if (Environment.TickCount64 - placing is >= 200 and var took) Log($"Overlay placed over the game in {took} ms ({bounds.Width}x{bounds.Height})");

        // A scan of a differently sized frame no longer lines up with the window.
        bool stale = _frame != null && (_frame.Width != bounds.Width || _frame.Height != bounds.Height);
        if (stale != _stale)
        {
            _stale = stale;
            RefreshOverlay();
        }
    }

    /// <summary>
    /// This window and the overlay out of the screen copies where Windows can do that (2004 and later): the game behind them is
    /// read. Before that the overlay blinks for each copy, and this window has to be off the game.
    /// </summary>
    private void LeaveOutOfCopies(ScreenCopyCapture copy)
    {
        bool can = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
        IntPtr me = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (can && Native.SetWindowDisplayAffinity(me, Native.WDA_EXCLUDEFROMCAPTURE)) copy.Ignore(me);
        IntPtr overlay = new System.Windows.Interop.WindowInteropHelper(_overlay).EnsureHandle();
        if (!can || !Native.SetWindowDisplayAffinity(overlay, Native.WDA_EXCLUDEFROMCAPTURE)) copy.CloakForCopies(overlay);
    }

    private void BackInCaptures()
    {
        Native.SetWindowDisplayAffinity(new System.Windows.Interop.WindowInteropHelper(this).Handle, Native.WDA_NONE);
        Native.SetWindowDisplayAffinity(new System.Windows.Interop.WindowInteropHelper(_overlay).Handle, Native.WDA_NONE);
    }

    private bool _stale;
    private readonly RunMemory _run = new();
    private readonly HintService _hints;
    private Hint? _hint;
    // When the hint shown came out.
    private DateTime _hintShownAt;
    private GameLogWatcher? _gameLog;
    private volatile string? _gameLogPath;
    /// <summary>The game's log followed (any thread may read it: a hang report takes its run).</summary>
    public string? GameLogPath => _gameLogPath;
    private readonly PickVotes _pickVotes = new();
    private readonly Tartarus.Rules.Engine _rulesEngine = new();
    /// <summary>When the Sandbox was last seen (a frame's capture time), and the pick screen's request to look at it again.</summary>
    private DateTime _lastBoardAt;
    // A plain Sandbox's presence (0.55-0.72): no popup, fade or red screen over it.
    private const double PlainPresence = 0.5;

    /// <summary>The run going on, as the game's log tells it; null without a log or between runs.</summary>
    private int _logSearch;
    private LoggedRun? LiveLogRun => _logRun is { Finished: false } run && (run.MaxDays > 0 || run.Paged) ? run : null;
    private LoggedRun? _logRun;
    private bool _logRunFinished;

    private void RefreshOverlay()
    {
        bool stale = _stale;
        _overlay.View.Model = new OverlayModel
        {
            FrameWidth = _frame?.Width ?? 0,
            FrameHeight = _frame?.Height ?? 0,
            Scan = stale ? null : _scan,
            Sandbox = _sandbox,
            Selected = _selected,
            EditMode = _editMode,
            ShowRegions = BoxesCheck.IsChecked == true,
            ShowText = TextCheck.IsChecked == true,
            KeptInstruments = stale ? new HashSet<int>() : new HashSet<int>(_run.Kept),
            Hint = stale || !_settings.Hints ? null : _hint,
            Status = stale ? Strings.Status_WindowResized : _status,
            // The game keeps the picture it drew before its window was resized by the edges: its layout is only
            // guessed (2026-10-30, a player's report: nothing read).
            Warning = _scan?.Notes?.Any(n => n.StartsWith(Vision.GamePicture.Note, StringComparison.Ordinal)) == true
                ? Strings.Status_PictureDoesntFill
                : null,
            LanguageAsk = _textUnread && _scan is { Ok: true } ? LanguageAsk : null,
            EyeHint = !stale && _settings.Hints && _flow.EyeHint,
            HintsOnly = _settings.Hints && _settings.HintsOnly,
            HintLine = _settings.Debug,
        };
    }

    // ---------- Scanning ----------

    private async Task<bool> ScanAsync(bool manual)
    {
        if (_capture == null)
        {
            if (manual) ScanStatus.Text = Strings.Status_NoWindowCaptured;
            return false;
        }
        if (_scans.Busy || (_editMode && !manual)) return false;
        if (_target is { } t && Win32Windows.IsMinimized(t.Hwnd)) return false;
        // Off the UI thread: the copy waits on the graphics card, which may take its time (or never answer).
        var capture = _capture;
        Frame? frame;
        try { frame = await Task.Run(() => capture.Grab(_pixels)); }
        catch (Exception ex) when (capture is ScreenCopyCapture)
        {
            ScanStatus.Text = "Screen copy failed: " + ex.Message;
            Log("Screen copy failed: " + ex.Message);
            return false;
        }
        catch (Exception ex) when (WindowCapture.IsDeviceLost(ex))
        {
            // The graphics card was reset under the capture (2026-10-31, Sentry: auto scan stopped for good): a new
            // capture of the same window.
            if (capture != _capture) return false;
            Log($"Capture: the graphics device was lost (reason {(capture as WindowCapture)?.RemovedReason}), capturing again ({ex.Message.Trim()})");
            if (++_lostWithoutFrame >= LossesForScreenCopy && !_screenCopy)
            {
                Log($"Capture: {_lostWithoutFrame} times lost with no frame between. A copy of the screen from now on.");
                _screenCopy = true;
            }
            var target = _target;
            // The overlay stays: hiding a window while the graphics card resets waits on the desktop compositor (2026-11-01,
            // Sentry: the UI thread stood 10 s in ShowWindow).
            StopCapture(hideOverlay: false);
            if (target is { } w && Win32Windows.Exists(w.Hwnd)) StartCapture(w, keepOverlay: true);
            return false;
        }
        if (capture != _capture) return false;
        if (capture is ScreenCopyCapture { Covered: var covered } && covered != _copyCovered)
        {
            _copyCovered = covered;
            CaptureStatus.Text = covered ? Strings.Status_ScreenCopyCovered : Strings.Status_ScreenCopyOn;
        }
        if (frame == null)
        {
            if (manual) ScanStatus.Text = Strings.Status_NoFramesYet;
            return false;
        }
        _lostWithoutFrame = 0;
        ScanResult r;
        try
        {
            r = await _scans.ScanAsync(frame, _gameLog?.Log.UiScale);
        }
        catch (Exception ex)
        {
            Log("Scan error: " + ex.Message);
            return false;
        }
        if (_editMode && !manual) return false;
        // What the game's log tells is read whole: its watcher's thread writes the same run while this reads it.
        lock (LogLock) TakeScan(frame, r, manual);
        return true;
    }

    /// <summary>
    /// The lock the log's watcher changes its run under (<see cref="GameLogWatcher"/>): the window reads a dozen of the
    /// run's values for one hint, and read between two of the watcher's writes they told of no moment that ever was
    /// (2026-11-11 review).
    /// </summary>
    private object LogLock => (object?)_gameLog?.Log ?? _noLog;
    private readonly object _noLog = new();

    /// <summary>A scan's reading taken in: steadied against the last ones, the run's memory, the overlay, the hint.</summary>
    private void TakeScan(Frame frame, ScanResult r, bool manual)
    {
        if (manual && _sandbox?.AnyEdited == true && !_savedSinceScan) Log("Unsaved edits replaced by a new scan");

        // Cells a tooltip covers keep what the last scan of the same Sandbox read there. Only a Sandbox read whole is
        // kept to: the first frame after another screen is often caught mid-fade (no Warden, odd cells), and the next
        // good one would be held to it.
        RememberFrame(frame);
        var previous = r.Ok && _scan?.Ok == true && _frame is { } last && last.Width == frame.Width && last.Height == frame.Height
            && _scannedSandbox is { } before && HintRules.WholeBoard(before) ? before : null;
        _frame = frame;
        _stale = false;
        _scan = r;
        var read = EditableSandbox.FromScan(r);
        _scannedSandbox = read.Clone();
        if (previous != null) _scannedSandbox.KeepCovered(previous);
        // A hint settles on this scan's own reading, not the steadied one: Steady holds a cell's change back a scan of
        // its own, and the settling a second (a door opening waited three scans for its hint, 2026-11-01).
        _settleRead = _scannedSandbox.Clone();
        if (previous != null && _lastRead != null) _scannedSandbox.Steady(previous, _lastRead);
        _lastRead = read;
        if (r.Ok && HintRules.WholeBoard(_scannedSandbox)) _lastBoardAt = frame.Timestamp.LocalDateTime;
        // The Consume target pick: the Sandbox stands still under the flickering red, so each Inmate shows its most
        // frequent reading over the pick's frames.
        bool targetPick = r.Ok && (r.Text?.ConsumePick == true || LiveLogRun is { ConsumePick: true } && r.Text?.Cards.Count == 0);
        if (targetPick)
        {
            // The red flickers: there the votes are the reading.
            _pickVotes.Apply(_scannedSandbox);
            _settleRead = _scannedSandbox.Clone();
        }
        else
        {
            _pickVotes.Clear();
            if (r.Ok && HintRules.WholeBoard(_scannedSandbox)) _pickVotes.Before(_scannedSandbox);
        }
        // Instruments and Chromatic: what this run has gathered, not just this frame's panels (a tooltip, a fade or
        // an animation may spoil them).
        if (r.Ok)
        {
            _run.Days(read.Days);
            _scannedSandbox.Instruments = [.. _run.Instruments(read.Instruments)];
            // A Tusk or Prey mark as the instruments held can make it, on the overlay and in the frames collected too.
            _scannedSandbox.MarksByInstruments();
            _scannedSandbox.Chromatic = _run.ChromaticCount(read.Chromatic, r.Chromatic?.Covered == true, frame.Timestamp);
            // The log's count can miss one (a Chromatic's sound in a quick chain) but never adds one: the higher of the two.
            if (LiveLogRun is { FromStart: true } logged) _scannedSandbox.Chromatic = Math.Max(_scannedSandbox.Chromatic, logged.ChromaticAtLeast);
            _scannedSandbox.Theme = _run.Theme;
            // The small mouth over an Inmate pulses in the game, and its reading with it; it means "has a target",
            // which the rules tell from the cells and instruments read.
            var rules = _rulesEngine.FromObservation(Tartarus.Vision.GameObservation.From(_scannedSandbox), 1);
            for (int n = 2; n <= 16; n++)
                if (_scannedSandbox[n].Kind == CellKind.Inmate) _scannedSandbox[n].Consumable = Tartarus.Rules.Engine.HasTarget(rules, n - 1);
        }
        else if (r.Theme is { } theme)
        {
            _run.SawTheme(theme);
            if (_logRun is { Finished: false } logged)
            {
                KeepTheme(logged, theme);
                PagedDays(logged);
            }
        }
        _sandbox = _scannedSandbox.Clone();
        _selected = -1;
        _savedSinceScan = false;
        _status = r.Ok ? null : manual || _editMode
            ? Loc.F(Strings.Status_NoSandboxFound, r.TotalMs.ToString("0", CultureInfo.InvariantCulture)) : null;
        RefreshOverlay();
        RefreshLabelsUi();
        ScanStatus.Text = (r.Ok
                ? Loc.F(Strings.Status_Sandbox, r.TotalMs.ToString("0", CultureInfo.InvariantCulture))
                : Loc.F(Strings.Status_NoSandbox, Loc.Name(r.Reason ?? ""), r.TotalMs.ToString("0", CultureInfo.InvariantCulture)))
            + (r.Timings.TryGetValue("ocr", out var ocrMs)
                ? Loc.F(Strings.Status_TimingsUpper, ocrMs.ToString("0", CultureInfo.InvariantCulture),
                    r.Timings.GetValueOrDefault("cells").ToString("0", CultureInfo.InvariantCulture))
                : "")
            + (r.Timings.ContainsKey("presence") ? Loc.F(Strings.Status_Presence, (r.Loc?.Score ?? 0).ToString("0.00", CultureInfo.InvariantCulture)) : "")
            + Loc.F(" · {0}×{1} · {2}", frame.Width, frame.Height, r.Source);
        string description = SandboxText.Describe(r);
        // What was read, without the scan's timing and presence score (they differ every scan).
        string reading = r.Ok ? SandboxText.Describe(_scannedSandbox) : description;
        if (manual || reading != _lastAutoDescription) Log((manual ? "Scan: " : "Changed: ") + description.Replace(Environment.NewLine, " | ").Replace("\n", " | "));
        _lastAutoDescription = reading;
        if (!manual) AutoCollect(frame, r);
        // Only a plain Sandbox tells whether the text reads: the red screen and a fade darken the Objective's panel
        // (2026-11-01: a chain of Consumes asked for the game's language, 15 red frames in 5 s).
        if (r.Ok && !targetPick && LiveLogRun is not { ConsumePick: true } && r.Loc is { Score: >= PlainPresence })
        {
            if (r.Text != null) CheckTextReadable(r.Text, frame.Timestamp.LocalDateTime);
            // The theme's icon, not on a run that's over: the result's Sandbox would carry its theme into the next.
            if (_logRun is not { Finished: true } && _run.ThemeIcon(r.ThemeIcon) && _run.Theme is { } iconTheme)
            {
                Log($"Run theme: {RunThemes.NameOf(iconTheme)} (its icon)");
                if (_logRun is { } running)
                {
                    KeepTheme(running, iconTheme);
                    PagedDays(running);
                }
            }
        }
        RequestHint(r);
    }

    // ---------- Hint quality ----------

    private IReadOnlyList<Tartarus.Solver.HintLevel> _hintLevels = Tartarus.Solver.HintLevels.Default;
    // The slider is set up: its events are the player's (XAML fires one at load).
    private bool _hintSliderReady;

    private void SetupHintSlider()
    {
        _hintLevels = Tartarus.Solver.HintLevels.Load();
        HintSlider.Maximum = _hintLevels.Count - 1;
        int index = 0;
        for (int i = 0; i < _hintLevels.Count; i++)
            if (_hintLevels[i].Playouts <= _settings.HintPlayouts) index = i;
        HintSlider.Value = index;
        HintsCheck.IsChecked = _settings.Hints;
        HintsOnlyCheck.IsChecked = _settings.HintsOnly;
        ShowHintLevel();
        _hintSliderReady = true;
    }

    private void ShowHintLevel()
    {
        var level = _hintLevels[Math.Clamp((int)HintSlider.Value, 0, _hintLevels.Count - 1)];
        HintLevelText.Text = Loc.F(Strings.Overlay_QualityEstimate,
            level.Milliseconds.ToString("0.#", CultureInfo.InvariantCulture),
            level.Chromatic.ToString("0.0", CultureInfo.InvariantCulture),
            level.Playouts.ToString("N0", CultureInfo.InvariantCulture));
    }

    private void HintSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_hintSliderReady || _hints == null || _hintLevels.Count == 0) return;
        var level = _hintLevels[Math.Clamp((int)HintSlider.Value, 0, _hintLevels.Count - 1)];
        _settings.HintPlayouts = level.Playouts;
        _hints.Playouts = level.Playouts;
        ShowHintLevel();
    }

    private void HintsOnlyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_hintSliderReady) return;
        _settings.HintsOnly = HintsOnlyCheck.IsChecked == true;
        RefreshOverlay();
    }

    private void HintsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_hintSliderReady) return;
        _settings.Hints = HintsCheck.IsChecked == true;
        if (!_settings.Hints)
        {
            _hints.Clear();
            ClearHint();
        }
        RefreshOverlay();
    }

    // ---------- The game's log ----------

    /// <summary>
    /// Follows UE_game.log: the exact day, a new run's start, each run's result. With the captured game's executable
    /// (<paramref name="gameExe"/>) the log beside it, which is the running install's for sure.
    /// </summary>
    private async void StartGameLog(string? gameExe = null)
    {
        try
        {
            _lastGameExe = gameExe ?? _lastGameExe;
            // Off the UI thread: it looks at files on every drive a game may be on, and a slow one held the window's
            // start for 30 s (2026-11-10, a player's hang report). A later call's answer is the one that counts.
            int search = ++_logSearch;
            string? configured = _settings.GameLogPath, exe = _lastGameExe;
            var path = await Task.Run(() => GameLog.FindPath(configured, exe));
            if (search != _logSearch) return;
            if (string.Equals(path, _gameLogPath, StringComparison.OrdinalIgnoreCase)) return;
            _gameLog?.Dispose();
            _gameLog = null;
            _gameLogPath = path;
            _logRun = null;
            // Once the log has told a run (OnLogRun) it tells runs apart, and the day count's reading no longer has to.
            // Until then the screen alone: a log that names no run (another client's, another game's) must not keep the
            // last run's Chromatics in the next.
            _run.NewRunByDays = true;
            if (path != null)
            {
                var watcher = _gameLog = new GameLogWatcher(path);
                watcher.Changed += run => Dispatcher.BeginInvoke(() =>
                {
                    if (_gameLog == watcher) OnLogRun(run);
                });
                watcher.Activity += () => Dispatcher.BeginInvoke(() =>
                {
                    if (_gameLog == watcher) OnLogActivity();
                });
            }
            Log(path != null ? $"Game log: {path}" : "Game log not found: screen recognition only");
        }
        catch (Exception ex)
        {
            Log("Game log unreadable: " + ex.Message);
        }
        finally
        {
            ShowLogStatus();
        }
    }

    private string? _lastGameExe;

    // The run theme shows on a scroll only at the run's start: what was read is kept with the run's start (as the log
    // has it), so the app started again mid-run knows it still.
    private static string ThemeFile => Path.Combine(AppSettings.Folder, "run-theme.txt");
    private (LoggedRun Run, int MaxDays)? _themeChecked;

    private static void KeepTheme(LoggedRun run, string theme)
    {
        try { File.WriteAllLines(ThemeFile, [run.Start.ToString("o"), theme]); }
        catch (IOException) { }
    }

    /// <summary>The run's theme when the app doesn't know it: the one kept for this run, or Iron Law by the run's 30 days.</summary>
    private void RestoreTheme(LoggedRun run)
    {
        if (run.Finished || _run.Theme != null || _themeChecked is { } c && c.Run == run && c.MaxDays == run.MaxDays) return;
        _themeChecked = (run, run.MaxDays);
        string? theme = null;
        try
        {
            if (File.Exists(ThemeFile) && File.ReadAllLines(ThemeFile) is [var start, var kept] && start == run.Start.ToString("o")) theme = kept;
        }
        catch (IOException) { }
        theme ??= run.MaxDays == 30 ? "ironlaw" : null;
        if (theme == null) return;
        _run.SawTheme(theme);
        Log($"Run theme: {RunThemes.NameOf(theme)} (remembered for this run)");
    }

    /// <summary>A run the log tells by its pages, its theme's scroll seen: 30 days for Iron Law, else 20, once the theme is read.</summary>
    private void PagedDays(LoggedRun run)
    {
        if (run is { Paged: true, ThemeShown: true, MaxDays: 0 } && _run.Theme is { } theme) run.MaxDays = theme == "ironlaw" ? 30 : 20;
    }

    private void ShowLogStatus() => LogStatus.Text =
        (_gameLogPath ?? Strings.Main_LogNotFound)
        + (_settings.GameLogPath != null ? Strings.Main_LogChosenByHand : Strings.Main_LogFoundAutomatically);

    private void PickLogButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.Main_GameLog_Tip,
            Filter = "Game log (UE_game.log)|UE_game.log|Logs (*.log)|*.log",
            InitialDirectory = _gameLogPath != null ? Path.GetDirectoryName(_gameLogPath) : null,
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.GameLogPath = dialog.FileName;
        _settings.Save();
        StartGameLog();
    }

    private void AutoLogButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.GameLogPath = null;
        _settings.Save();
        StartGameLog();
    }

    private void OnLogRun(LoggedRun run)
    {
        lock (LogLock) TakeLogRun(run);
    }

    private void TakeLogRun(LoggedRun run)
    {
        bool newRun = !ReferenceEquals(run, _logRun);
        _logRun = run;
        _run.NewRunByDays = false;
        if (newRun)
        {
            _logRunFinished = false;
            _flow.Rerolls = null;
            // The log saw the run start: what the app kept of the last run goes (instruments, Chromatic, the hint).
            if (run.FromStart)
            {
                _run.NewRun();
                _flow.TextBeforeRed = null;
                _hints.Clear();
                ClearHint();
                // A theme read just before the log had the run: kept with this run.
                if (_run.Theme is { } coming) KeepTheme(run, coming);
            }
            Log(run.FromStart ? "Log: new run" : $"Log: run in progress (day {run.Day} of {run.MaxDays})");
        }
        RestoreTheme(run);
        PagedDays(run);
        if (run.MaxDays > 0) _run.Days(run.RemainingDays);
        if (run.Finished && !_logRunFinished)
        {
            _logRunFinished = true;
            Log($"Log: run finished — Chromatic {run.Chromatic}, Prison Incidents {run.Incidents}, Consume {run.Consumes}");
            _run.ThemeOver();
        }
        CountRuns();
    }

    // ---------- Move hints ----------

    /// <summary>The position on screen goes to the hint search: a Sandbox as scanned (with the run's memory), a pick over the last Sandbox seen.</summary>
    /// <summary>The hint on screen no longer fits: gone until the new position's is ready.</summary>
    private void ClearHint()
    {
        if (_hint == null) return;
        _hint = null;
        RefreshOverlay();
    }

    private void RequestHint(ScanResult r)
    {
        _flow.ConfirmSoon = false;
        if (!_settings.Hints || _editMode) return;
        try
        {
            _flow.Take(r, new HintInput(_sandbox, _settleRead, _lastRead, _lastBoardAt, LiveLogRun, _logRun, _run, DateTime.Now, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            // With its stack, and reported once a kind: only the message was logged, and a fault here is "no hints" with
            // nobody the wiser (2026-11-11 review).
            Log("Hint failed: " + ex);
            if (_hintErrors.Add(ex.GetType().Name + ex.TargetSite?.Name)) BugReports.Crash(ex);
        }
    }

    private readonly HashSet<string> _hintErrors = [];

    private readonly HintFlow _flow;
    bool IHintSink.Request(Tartarus.Rules.Observation o, bool consumeOnly, bool huntOnly, bool ahead) => _hints.Request(o, consumeOnly, huntOnly, ahead);
    void IHintSink.Clear()
    {
        _hints.Clear();
        ClearHint();
    }
    void IHintSink.ClearShown() => ClearHint();
    void IHintSink.Log(string message) => Log(message);
    void IHintSink.EyeChanged(bool now)
    {
        if (now) RefreshOverlay();
        else Dispatcher.BeginInvoke(RefreshOverlay);
    }
    string IHintSink.BackToPick => Strings.Overlay_SandboxReadBackToPick;
    DateTime? IHintSink.HuntShownAt =>
        _hint?.Ranked is { Count: > 0 } shown && shown[0].Action.Kind == Tartarus.Rules.ActionKind.Hunt ? _hintShownAt : null;

    // ---------- Automatic dataset collection ----------

    private string? _lastCollected;
    private DateTime _lastCollectedAt;
    private bool _collecting;

    /// <summary>Saves the frame as a sample when the Sandbox's reading changed since the last automatic one.</summary>
    private void AutoCollect(Frame frame, ScanResult r)
    {
        if (r.Theme is { } theme)
        {
            CollectScreen(frame, r, "theme: " + theme);
            return;
        }
        if (r.Pick is { } pick)
        {
            CollectPick(frame, r, pick);
            return;
        }
        if (!_settings.AutoCollect || !r.Ok || _editMode || _collecting || _sandbox == null) return;
        string key = SandboxText.Describe(_sandbox);
        if (key == _lastCollected || (DateTime.Now - _lastCollectedAt).TotalSeconds < _settings.AutoCollectSeconds) return;
        _lastCollected = key;
        _lastCollectedAt = DateTime.Now;
        _collecting = true;
        var (sandbox, title) = (_sandbox.Clone(), _target?.Title);
        frame.Keep();
        _ = Task.Run(() => _dataset.Save(frame, r, sandbox, title, auto: true)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            _collecting = false;
            if (t.Exception is { } ex) Log("Auto-collect: could not save: " + ex.InnerException?.Message);
            else
            {
                Log($"Auto-collect: {t.Result}");
                UpdateDatasetStatus();
            }
        }));
    }

    private string? _lastPick;

    /// <summary>Saves a Torture Instrument pick screen to the dataset's picks/, once per offer (a Re-roll is a new one).</summary>
    private void CollectPick(Frame frame, ScanResult r, PickRead pick)
    {
        // The treasures' icons come from the first pick screen that shows them (with or without collecting).
        if (pick.Kind == PickRead.Treasure && r.Layout is { } layout && frame.Keep() is var kept)
            _ = Task.Run(() => WardenTreasures.SaveMissingIcons(kept, layout, pick)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                if (t.Exception is { } ex) Log("Warden's Treasure icons: could not save: " + ex.InnerException?.Message);
                else if (t.Result.Count > 0) Log("Warden's Treasure icons saved: " + string.Join(", ", t.Result));
            }));
        // The cards only: the Re-roll counter shows a moment after them, and a Re-roll brings new cards anyway.
        CollectScreen(frame, r, pick.Kind + ": " + string.Join(",", pick.Cards.Select(c => c.Id ?? "?")));
    }

    /// <summary>Saves a screen other than the Sandbox (a pick, the run theme) to the dataset's picks/, once per <paramref name="key"/> in a row.</summary>
    private void CollectScreen(Frame frame, ScanResult r, string key)
    {
        if (!_settings.AutoCollect || _editMode || key == _lastPick) return;
        _lastPick = key;
        frame.Keep();
        _ = Task.Run(() => _dataset.SavePick(frame, r)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            Log(t.Exception is { } ex ? "Auto-collect: could not save the screen: " + ex.InnerException?.Message : $"Auto-collect: screen {t.Result} ({key})")));
    }

    private void AutoCollectCheck_Changed(object sender, RoutedEventArgs e) => _settings.AutoCollect = AutoCollectCheck.IsChecked == true;

    private void TextCheck_Changed(object sender, RoutedEventArgs e)
    {
        _settings.ShowTextBoxes = TextCheck.IsChecked == true;
        RefreshOverlay();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanAsync(manual: true);

    private void AutoScanCheck_Changed(object sender, RoutedEventArgs e)
    {
        _settings.AutoScan = AutoScanCheck.IsChecked == true;
        if (_settings.AutoScan) _ = AutoScanLoopAsync();
        else _autoScanLoop++;
    }

    /// <summary>
    /// Scans come this soon after the last one's start, not a whole period: to confirm a reading not settled yet (a hint
    /// waits for two alike), and for <see cref="BurstFor"/> after the game's log tells of a click or a page opening or
    /// closing (the screen is about to change). A tenth of a second: the confirming reading was a fifth of every hint's
    /// wait (2026-11-01); a slow PC rests as long as its scan took anyway.
    /// </summary>
    private static readonly TimeSpan QuickScan = TimeSpan.FromSeconds(0.1);
    /// <summary>A scan (with its hint) longer than this rests as long again before the next: a slow PC's.</summary>
    private const double SlowScan = 0.25;
    // The last scan went without its rest, to confirm a reading.
    private bool _restSkipped;
    private static readonly TimeSpan BurstFor = TimeSpan.FromSeconds(1.5);
    private DateTime _burstUntil;
    /// <summary>
    /// Off the board (neither a Sandbox nor a pick read) for <see cref="OffBoardAfter"/>: the menus, the map, the Hideout
    /// are scanned only this often; the game's log calling (a click, a page) brings the usual pace back at once.
    /// </summary>
    private static readonly TimeSpan OffBoardScan = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OffBoardAfter = TimeSpan.FromSeconds(3);
    // The first of the scans in a row that read no board; null while on it.
    private DateTime? _offBoardSince;
    // Completed by the game's log: the scan loop's wait ends at once.
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The game's log told of a click or a page: scan now and often for a moment.</summary>
    private void OnLogActivity()
    {
        _burstUntil = DateTime.Now + BurstFor;
        _offBoardSince = null;
        _wake.TrySetResult();
    }

    /// <summary>
    /// Auto scan: a frame every <see cref="AppSettings.AutoScanSeconds"/> from the last one's start (<see cref="QuickScan"/>
    /// to confirm a reading or after the log's activity), never before the last one's reading and hint are done (at 0,
    /// as soon as they are).
    /// </summary>
    private async Task AutoScanLoopAsync()
    {
        int loop = ++_autoScanLoop;
        while (loop == _autoScanLoop)
        {
            if (_wake.Task.IsCompleted) _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = Stopwatch.StartNew();
            bool scanned;
            try
            {
                scanned = await ScanAsync(manual: false);
                await _hints.SearchingConfirmed;
            }
            catch (Exception ex)
            {
                // As the app's own errors: logged, offered as a report; the loop stops (no error a frame).
                Log("Auto scan stopped: " + ex);
                AutoScanCheck.IsChecked = false;
                if (BugReports.Enabled) Report(ex);
                return;
            }
            if (scanned) _offBoardSince = _scan is { } last && (last.Ok || last.Pick != null) ? null : _offBoardSince ?? DateTime.Now;
            // A reading to confirm is the Sandbox's: one left unconfirmed as the board went away (a run's last frame, faded)
            // kept the scans coming every 0.1 s off the board, the pick search and all (2026-11-01: 1.24 cores in the lobby).
            bool confirm = _flow.ConfirmSoon && _scan is { Ok: true };
            bool offBoard = _offBoardSince is { } off && DateTime.Now - off >= OffBoardAfter && !confirm && DateTime.Now >= _burstUntil;
            // Nothing to scan (no window, minimized, editing): no spinning at 0.
            double period = offBoard ? OffBoardScan.TotalSeconds : _settings.AutoScanSeconds;
            if (!offBoard && scanned && (confirm || DateTime.Now < _burstUntil)) period = Math.Min(period, QuickScan.TotalSeconds);
            // A slow scan and its hint rest at least as long as they took: on a slow PC (a Sandbox read in 1.3 s) they ran
            // back to back, and the game had what was left. Not a quick one: the rest held every confirming reading back
            // ~0.1 s on a fast PC (2026-11-01: a new day's hint 0.27 s after its first reading with cards, 0.17 before).
            // Nor before the reading that confirms a Sandbox, every other scan at most: the rest stood between a slow PC's
            // first reading and its hint (2026-11-13, an i5-8300H laptop: readings 2 s apart, 47 of 82 moves made before
            // any hint), while a Sandbox that won't settle still leaves the game half the time.
            double busy = started.Elapsed.TotalSeconds;
            bool noRest = confirm && !_restSkipped;
            _restSkipped = noRest;
            double wait = Math.Max(Math.Max(period, scanned ? 0 : 0.2) - busy, scanned && busy > SlowScan && !noRest ? busy : 0);
            // The log's activity cuts the wait short (only a scan's own time apart: not when nothing was scanned).
            var delay = Task.Delay(TimeSpan.FromSeconds(Math.Max(wait, 0.001)));
            await (scanned ? Task.WhenAny(delay, _wake.Task) : delay);
        }
    }

    private void IntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings == null) return;
        _settings.AutoScanSeconds = Math.Round(IntervalSlider.Value, 1);
        ShowInterval();
    }

    private void ShowInterval() =>
        IntervalText.Text = _settings.AutoScanSeconds > 0 ? $"{_settings.AutoScanSeconds:0.0} s" : "0 s (as soon as done)";

    private void OverlayCheck_Changed(object sender, RoutedEventArgs e)
    {
        _settings.OverlayVisible = OverlayCheck.IsChecked == true;
        PlaceOverlay();
    }

    private void BoxesCheck_Changed(object sender, RoutedEventArgs e)
    {
        _settings.ShowRegions = BoxesCheck.IsChecked == true;
        RefreshOverlay();
    }

    // ---------- Editing labels ----------

    private void EditCheck_Changed(object sender, RoutedEventArgs e)
    {
        _editMode = EditCheck.IsChecked == true;
        _overlay.SetClickThrough(!_editMode);
        if (!_editMode)
        {
            _selected = -1;
            if (_sandbox?.AnyEdited == true && !_savedSinceScan) Log($"Edits not saved ({_settings.Hotkeys.GetValueOrDefault("save", "Save frame")} saves the frame with them)");
        }
        else if (_scan?.Ok != true) Log("Edit: scan a frame with a Sandbox first");
        RefreshOverlay();
    }

    private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode || _sandbox == null) return;
        int idx = _overlay.View.CellAt(e.GetPosition(_overlay.View));
        if (idx < 0) return;
        _selected = idx;
        RefreshOverlay();
        var menu = BuildCellMenu(idx);
        menu.PlacementTarget = _overlay.View;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Closed += (_, _) =>
        {
            _selected = -1;
            RefreshOverlay();
        };
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildCellMenu(int idx)
    {
        var sandbox = _sandbox!;
        var cell = sandbox.Cells[idx];
        int n = idx + 1;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = Loc.F(Strings.Hint_CellDetail, n, SandboxText.CellShort(cell)), IsEnabled = false });
        menu.Items.Add(new Separator());

        if (idx > 0)
        {
            menu.Items.Add(Item("Opened, empty", cell.Kind == CellKind.Empty, () => SetKind(cell, CellKind.Empty)));
            menu.Items.Add(Item("Unopened", cell.Kind == CellKind.Unopened, () => SetKind(cell, CellKind.Unopened)));
            menu.Items.Add(new Separator());
            // An Inmate: its Code, then its rarity (Normal .. Ultimate: Chromatic leaves the Sandbox).
            foreach (var code in Codes.All)
            {
                var sub = new MenuItem { Header = Loc.Name(Codes.Name(code)), IsChecked = cell.Kind == CellKind.Inmate && cell.Code == code };
                for (var rarity = Rarity.Normal; rarity <= Rarity.Ultimate; rarity++)
                {
                    var r = rarity;
                    var item = Item(r.ToString(), cell.Kind == CellKind.Inmate && cell.Code == code && cell.Rarity == r, () =>
                    {
                        cell.Kind = CellKind.Inmate;
                        cell.Code = code;
                        cell.Rarity = r;
                        cell.Nest = false;
                    });
                    item.Foreground = SandboxOverlayView.RarityBrush(r);
                    sub.Items.Add(item);
                }
                menu.Items.Add(sub);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Consumable target", cell.Consumable, () => cell.Consumable = !cell.Consumable));
            menu.Items.Add(Item("Tusk (Iron Tongs of Lust: Mammoth)", cell.Tusk, () => cell.Tusk = !cell.Tusk));
            menu.Items.Add(Item("Prey (Hunt, Blood-Stained Trap)", cell.Prey, () => cell.Prey = !cell.Prey));
            menu.Items.Add(Item("Rat Nest (Squirming Iron Box)", cell.Nest, () => cell.Nest = !cell.Nest));
            var visibleMenu = new MenuItem { Header = Strings.Edit_VisibleRarity, IsChecked = cell.VisibleRarity != null };
            visibleMenu.Items.Add(Item("none", cell.VisibleRarity == null, () => cell.VisibleRarity = null));
            for (var rarity = Rarity.Normal; rarity <= Rarity.Ultimate; rarity++)
            {
                var t = rarity;
                var item = Item(t.ToString(), cell.VisibleRarity == t, () =>
                {
                    SetKind(cell, CellKind.Unopened);
                    cell.VisibleRarity = t;
                });
                item.Foreground = SandboxOverlayView.RarityBrush(t);
                visibleMenu.Items.Add(item);
            }
            menu.Items.Add(visibleMenu);
            menu.Items.Add(Item("Scratch Marks (Filthy Claws)", cell.ScratchMarks, () =>
            {
                SetKind(cell, CellKind.Unopened);
                cell.ScratchMarks = !cell.ScratchMarks;
            }));
            menu.Items.Add(Item("Prison Incident (!)", cell.Incident, () => cell.Incident = !cell.Incident));
            menu.Items.Add(Item("Covered by the UI (not judged)", cell.Covered, () => cell.Covered = !cell.Covered));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item("Warden here", sandbox.Warden == n, () =>
        {
            sandbox.Warden = n;
            sandbox.SandboxEdited = true;
        }, cellEdit: false));
        menu.Items.Add(Item(sandbox.Destinations.Contains(n) ? Loc.F(Strings.Edit_RemoveDestination, sandbox.Destinations.IndexOf(n) + 1) : Loc.F(Strings.Edit_AddDestination, sandbox.Destinations.Count + 1), sandbox.Destinations.Contains(n), () =>
        {
            if (!sandbox.Destinations.Remove(n)) sandbox.Destinations.Add(n);
            sandbox.Phase = sandbox.Destinations.Count > 0 ? Phases.Destination : Phases.Decision;
            sandbox.SandboxEdited = true;
        }, cellEdit: false));
        if (_scannedSandbox != null && idx > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Back to the scan", false, () => sandbox.Cells[idx] = _scannedSandbox.Cells[idx].Clone(), cellEdit: false));
        }
        return menu;

        MenuItem Item(string header, bool isChecked, Action apply, bool cellEdit = true)
        {
            var mi = new MenuItem { Header = Loc.Name(header), IsChecked = isChecked };
            mi.Click += (_, _) =>
            {
                apply();
                if (cellEdit) sandbox.Cells[idx].Edited = true;
                _savedSinceScan = false;
                RefreshOverlay();
                RefreshLabelsUi();
            };
            return mi;
        }

        static void SetKind(CellLabel c, CellKind kind)
        {
            c.Kind = kind;
            if (kind != CellKind.Inmate)
            {
                c.Code = null;
                c.Rarity = 0;
                c.Consumable = false;
                c.Tusk = false;
                c.Prey = false;
            }
            if (kind != CellKind.Unopened)
            {
                c.Nest = false;
                c.VisibleRarity = null;
                c.ScratchMarks = false;
            }
        }
    }

    private void RefreshLabelsUi()
    {
        var b = _sandbox;
        WardenBox.Text = b is { Warden: > 0 } ? b.Warden.ToString(CultureInfo.InvariantCulture) : "";
        DaysBox.Text = b?.Days?.ToString(CultureInfo.InvariantCulture) ?? "";
        DestinationsBox.Text = b == null ? "" : string.Join(",", b.Destinations);
        ChromaticBox.Text = b?.Chromatic.ToString(CultureInfo.InvariantCulture) ?? "";
        InstrumentsBox.Text = b == null ? "" : string.Join(",", b.Instruments);
        SandboxSummary.Text = b == null ? "" : Summary(b);

        static string Summary(EditableSandbox b) => SandboxText.Describe(b) + (b.AnyEdited ? "  (edited)" : "");
    }

    private void ApplyLabelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sandbox == null) return;
        static List<string> Split(string s) => s.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _sandbox.Warden = int.TryParse(WardenBox.Text, out var w) && w is >= 1 and <= 16 ? w : 0;
        _sandbox.Days = int.TryParse(DaysBox.Text, out var d) ? d : null;
        _sandbox.Destinations = Split(DestinationsBox.Text).Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v is >= 1 and <= 16).ToList();
        _sandbox.Phase = _sandbox.Destinations.Count > 0 ? Phases.Destination : Phases.Decision;
        _sandbox.Chromatic = int.TryParse(ChromaticBox.Text, out var c) ? c : 0;
        _sandbox.Instruments = Split(InstrumentsBox.Text);
        _sandbox.SandboxEdited = true;
        _savedSinceScan = false;
        RefreshOverlay();
        RefreshLabelsUi();
    }

    private void ResetLabelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scannedSandbox == null) return;
        _sandbox = _scannedSandbox.Clone();
        RefreshOverlay();
        RefreshLabelsUi();
    }

    // ---------- Dataset ----------

    private async Task SaveSampleAsync()
    {
        if (_frame == null || _scan == null || _sandbox == null)
        {
            Log($"Nothing to save: scan first ({_settings.Hotkeys.GetValueOrDefault("scan", "Scan")})");
            return;
        }
        var (frame, scan, sandbox, title) = (_frame.Keep(), _scan, _sandbox.Clone(), _target?.Title);
        try
        {
            var sw = Stopwatch.StartNew();
            string id = await Task.Run(() => _dataset.Save(frame, scan, sandbox, title));
            _savedSinceScan = true;
            int edited = sandbox.Cells.Count(c => c.Edited);
            Log($"Saved frame {id}{(scan.Ok ? "" : " (no Sandbox)")}, edits: {edited}{(sandbox.SandboxEdited ? " + Sandbox fields" : "")} · {sw.ElapsedMilliseconds} ms");
            UpdateDatasetStatus();
        }
        catch (Exception ex)
        {
            Log("Could not save: " + ex.Message);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await SaveSampleAsync();

    private void UpdateDatasetStatus()
    {
        int count = Directory.Exists(_dataset.FramesDir) ? Directory.EnumerateFiles(_dataset.FramesDir, "*.png").Count() : 0;
        DatasetStatus.Text = $"{_dataset.Root} · frames: {count}";
    }

    private void OpenDatasetButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_dataset.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dataset.Root}\"") { UseShellExecute = true });
    }

    // ---------- Log ----------

    private void Log(string message)
    {
        var now = DateTime.Now;
        string line = $"{now:HH:mm:ss} {message}";
        AppLog.Write(message);
        LogList.Items.Add(line);
        while (LogList.Items.Count > 200) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
