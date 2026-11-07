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
public partial class ControlWindow : Window
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
                + (_hints.Net != null ? Strings.Main_ValueNet : Strings.Main_NoValueNet) + (_hints.Policy != null ? Strings.Main_PolicyNet : "") + (_hints.Picks != null ? Strings.Main_PicksNet : "");
            RefreshOverlay();
        });
        _dataset = new Dataset(settings.DatasetDir);
        Directory.CreateDirectory(AppSettings.Folder);
        InitializeComponent();
        StartGameLog();
        SetupHintSlider();
        if (settings.ControlLeft is { } left && settings.ControlTop is { } top)
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
            : windows.FirstOrDefault(w => pattern.IsMatch(w.Title) && !Win32Windows.IsLauncher(w)) is { Hwnd: not 0 } game ? game
            : null;
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
            && new Regex(_settings.WindowTitle).IsMatch(target.Title) && !Win32Windows.IsLauncher(target))
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
    // The last Sandbox the hints saw (with the run's memory) and its text: a pick screen's hint plays on from it.
    private EditableSandbox? _hintSandbox;
    private Vision.Ocr.ScreenText? _hintText;
    // The log's run that text was read in.
    private LoggedRun? _hintTextRun;
    // The same scan as read, before Steady: the base of a pick that comes up right after a change.
    private EditableSandbox? _hintRead;
    private GameLogWatcher? _gameLog;
    private volatile string? _gameLogPath;
    /// <summary>The game's log followed (any thread may read it: a hang report takes its run).</summary>
    public string? GameLogPath => _gameLogPath;
    private readonly PickVotes _pickVotes = new();
    private readonly Tartarus.Rules.Engine _rulesEngine = new();
    /// <summary>When the Sandbox was last seen (a frame's capture time), and the pick screen's request to look at it again.</summary>
    private DateTime _lastBoardAt;
    // When the Sandbox the hints play on (_hintSandbox) was seen: the old one after the log's change leaves it older.
    private DateTime _hintBoardAt;
    private const double FadePresence = 0.45;
    // A plain Sandbox's presence (0.55-0.72): no popup, fade or red screen over it.
    private const double PlainPresence = 0.5;
    private static readonly TimeSpan ChromaticGoing = TimeSpan.FromSeconds(2.5);
    // Which Sandbox readings a hint is made of: read alike twice, and not the old Sandbox after the log's change.
    private readonly BoardSettle _boardSettle = new();
    // The last reading wasn't settled: the scan loop reads the next frame soon (QuickConfirm), not a period later.
    private bool _confirmSoon;
    // The reading the hint shown was asked for (its Warden, phase, Destinations, Warden's Inmate).
    private EditableSandbox? _hinted;
    // The Hunt card last offered: where the Warden stood, the day, the Sandbox and the text read with it.
    private (int Warden, int? Days, EditableSandbox Board, Vision.Ocr.ScreenText Text)? _huntOffer;
    // The hint is the eye button: the Sandbox changed under a pick (look at it), or it was read while the pick waits (go back).
    private bool _eyeHint;
    // The Warden's Inmate while the Replace card was offered: after the click the card leaves a moment before the new
    // Inmate shows.
    private (int Warden, Tartarus.Vision.Code? Code, Tartarus.Vision.Rarity Rarity, DateTime At)? _replaceOffer;
    // How long after its reveal's sound a Replace's new Inmate is in its cell on screen.
    private static readonly TimeSpan ReplaceReveal = TimeSpan.FromSeconds(0.4);

    /// <summary>The run going on, as the game's log tells it; null without a log or between runs.</summary>
    private LoggedRun? LiveLogRun => _logRun is { Finished: false } run && (run.MaxDays > 0 || run.Paged) ? run : null;
    private LoggedRun? _logRun;
    private bool _logRunFinished;
    // Re-rolls the run has left, as the last instrument pick showed them (shared by every pick of the run).
    private int? _rerolls;
    // The last counter read: in which run, how many it showed, the Re-rolls the log had seen clicked by then, and the
    // pick it was read on (the log's count of instrument picks, the cards offered).
    private (LoggedRun Run, int Left, int Used, int Pick, string Cards)? _rerollsRead;
    // The last pick whose cards all read: its kind and cards.
    private (string Kind, List<string> Ids)? _lastWholePick;
    // The run's Re-rolls clicked as last seen in the log, and the cards a Re-roll is taking away (until others come).
    private (LoggedRun Run, int Used)? _rerollsSeen;
    private readonly RerolledCards _rerolledCards = new();
    // The last Sandbox read before the Consume target pick's red screen.
    private Vision.Ocr.ScreenText? _textBeforeRed;
    private int? _daysBeforeRed;

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
            EyeHint = !stale && _settings.Hints && _eyeHint,
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
            Log("Capture: the graphics device was lost, capturing again (" + ex.Message.Trim() + ")");
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
        if (manual && _sandbox?.AnyEdited == true && !_savedSinceScan) Log("Unsaved edits replaced by a new scan");

        // Cells a tooltip covers keep what the last scan of the same Sandbox read there. Only a Sandbox read whole is
        // kept to: the first frame after another screen is often caught mid-fade (no Warden, odd cells), and the next
        // good one would be held to it.
        RememberFrame(frame);
        var previous = r.Ok && _scan?.Ok == true && _frame is { } last && last.Width == frame.Width && last.Height == frame.Height
            && _scannedSandbox is { } before && WholeBoard(before) ? before : null;
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
        if (r.Ok && WholeBoard(_scannedSandbox)) _lastBoardAt = frame.Timestamp.LocalDateTime;
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
            if (r.Ok && WholeBoard(_scannedSandbox)) _pickVotes.Before(_scannedSandbox);
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
        return true;
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
    private void StartGameLog(string? gameExe = null)
    {
        try
        {
            _lastGameExe = gameExe ?? _lastGameExe;
            var path = GameLog.FindPath(_settings.GameLogPath, _lastGameExe);
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
        bool newRun = !ReferenceEquals(run, _logRun);
        _logRun = run;
        _run.NewRunByDays = false;
        if (newRun)
        {
            _logRunFinished = false;
            _rerolls = null;
            // The log saw the run start: what the app kept of the last run goes (instruments, Chromatic, the hint).
            if (run.FromStart)
            {
                _run.NewRun();
                _textBeforeRed = null;
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

    /// <summary>The eye button framed or not: on a pick, look at the Sandbox; on the Sandbox viewed from one, back to it.</summary>
    private void ShowEye(bool on, string? note = null)
    {
        if (on == _eyeHint) return;
        _eyeHint = on;
        if (on && note != null) Log("Hint: " + note);
        Dispatcher.BeginInvoke(RefreshOverlay);
    }

    /// <summary>A Sandbox read whole: the Warden found and no more than a couple of cells under the interface.</summary>
    private static bool WholeBoard(EditableSandbox b) => b.Warden > 0 && b.Cells.Count(c => c.Covered) < 3;

    /// <summary>Another decision than the one hinted on <paramref name="was"/>: the Warden, the Destinations or the Warden's Inmate.</summary>
    /// <remarks>
    /// Only what the new reading shows: Destination cards not read on one frame are no move, nor the phase that goes to
    /// Make Your Decision without them (2026-11-01: a card under the mouse grew and went unread, and the Destination
    /// hint went away before it was clicked). A day's End brings new cards, a move another Warden.
    /// </remarks>
    private static bool MovedOn(EditableSandbox was, EditableSandbox now) =>
        was.Warden != now.Warden
        || now.Destinations.Count > 0 && !was.Destinations.SequenceEqual(now.Destinations)
        || now.Warden > 1 && !now[now.Warden].Covered && SandboxText.CellShort(was[now.Warden]) != SandboxText.CellShort(now[now.Warden]);

    private void RequestHint(ScanResult r)
    {
        _confirmSoon = false;
        if (!_settings.Hints || _editMode) return;
        try
        {
            // A Sandbox half hidden (a Chromatic's flight or a popup over it, the Warden not found): no position to
            // search, nor a base for the pick that may follow; the last whole one stays.
            // The Sandbox viewed from a pick (the eye button): back to the pick is the hint once a reading of it counts,
            // not as the Sandbox comes up (a quick look went back before it was read, and the pick asked again).
            // A log with clicks says when the eye button was clicked: the pick's own comings and goings reach the log
            // a moment after the screen (2026-10-31: "back to the pick" flashed over the Sandbox as a Warden's Treasure
            // came up and as it was taken).
            bool viewing = LiveLogRun is { PickOpen: true } open && (open.Paged || open.Looking);
            if (r.Ok && _sandbox != null && !WholeBoard(_sandbox))
            {
                if (viewing) ShowEye(false);
                return;
            }
            if (r.Ok && _sandbox != null)
            {
                // A Warden's Treasure taken (its Confirm the log's last change, the pick taken then too) may change nothing.
                var changed = LiveLogRun;
                var settle = _boardSettle.Take(_settleRead ?? _sandbox, _lastBoardAt, changed?.BoardChangedAt ?? default,
                    changed is { } c && c.BoardChangedAt == c.PickTakenAt ? BoardSettle.StaleForTreasure : null);
                // A reading not settled yet: the next frame is read soon, not a whole scan period later.
                _confirmSoon = settle == BoardSettle.Verdict.Unsettled;
                // A frame caught as a popup comes or goes (presence well below a plain Sandbox's 0.6-0.7) reads wrong where
                // it's dim: not taken unless the next scan reads it alike (2026-10-28: a Warden's Treasure hinted over
                // one at 0.22, two Ultimates read Normal and the Warden elsewhere).
                if (settle == BoardSettle.Verdict.Unsettled && r.Loc is { Score: < FadePresence })
                {
                    if (viewing) ShowEye(false);
                    return;
                }
                // A pick that covers the Sandbox plays on its latest reading, settled or not: the Warden's Treasure comes up
                // with the Chromatic's sound, before a reading settles (every Treasure asked for the eye button). The old
                // Sandbox still on screen after the log's change counts as seen before it: the pick asks for the eye button.
                _hintSandbox = _sandbox.Clone();
                // The day's Destinations with a Prison Incident marker: a move onto one that does the Objective shows the
                // Objective's pick before the incident plays out, and the marker is gone from under the pick.
                if (_sandbox.Destinations.Count > 0)
                    _markedDestinations = (_sandbox.Days, _sandbox.Destinations.Where(d => d > 1 && _sandbox[d].Incident).ToHashSet());
                // A look at the Sandbox from a pick (the eye button) comes after its animations: always the new one.
                if (settle != BoardSettle.Verdict.Stale || viewing) _hintBoardAt = _lastBoardAt;
                _hintRead = _lastRead?.Clone();
                _hintText = r.Text;
                _hintTextRun = _logRun;
                // The log knows the day: it stands in when OCR didn't read the counter or read it wrong. The screen
                // shows a new day about a second before the log has it, so one day fewer than the log's is the screen
                // being ahead, not a misread. A pick's hint plays on from this Sandbox too (a popup darkens the counter).
                var live = LiveLogRun;
                if (live is { MaxDays: > 0 } && (_hintSandbox.Days is not int d || (d != live.RemainingDays && d != live.RemainingDays - 1)))
                    _hintSandbox.Days = live.RemainingDays;
                // No log's day (none, or a run's length it doesn't tell): the count last read in this run, not none
                // (a red screen or a popup darkens it, and none would be taken for the run's first day).
                _hintSandbox.Days ??= _run.LastDays;
                // A run the log saw from its start: its Chromatic count, or the panel's when higher (a sound can go missing).
                if (live is { FromStart: true }) _hintSandbox.Chromatic = Math.Max(_hintSandbox.Chromatic, live.ChromaticAtLeast);
                // The Sandbox viewed from a pick is read now: back to the pick is the hint.
                ShowEye(viewing, Strings.Overlay_SandboxReadBackToPick);
                if (viewing)
                {
                    _hints.Clear();
                    ClearHint();
                    return;
                }
                // A reading not settled yet is searched ahead, its hint held until the next reading confirms it: the hint
                // shown stays meanwhile, unless the position moved on (2026-11-01: "Cell 16" stood over the Warden walking
                // into it, then went to the Consume card). The old Sandbox still on screen after the log's bite, Chromatic
                // or Prison Incident takes the hint away: it's for that one.
                bool ahead = settle == BoardSettle.Verdict.Unsettled;
                switch (settle)
                {
                    case BoardSettle.Verdict.Unsettled:
                        if (_hinted is { } was && _settleRead is { } now && MovedOn(was, now))
                        {
                            _hints.Clear();
                            ClearHint();
                        }
                        break;
                    case BoardSettle.Verdict.Stale:
                        _hints.Clear();
                        ClearHint();
                        return;
                }
                // Only a screen that says what it asks: the Consume target pick's banner, Destination cards read, or a
                // decision card. Frames in between (cards sliding in, a day's banner) would be taken for the wrong phase.
                bool destinations = _sandbox.Phase == Phases.Destination && _sandbox.Destinations.Count > 0;
                // The day's move clicked (the log tells): Destination cards still on screen are going away, and two quick
                // readings alike took one left for the only Destination (2026-11-01: "Cell 16" flashed twice after Cell
                // 2 was clicked). Not for long: cards still there 2 s after the click weren't left (2026-11-01: a click the
                // game logged but didn't take, and the Destination hint stayed away the rest of the day).
                if (destinations && live is { Moved: true } moved && _lastBoardAt > moved.MovedAt
                    && _lastBoardAt - moved.MovedAt < TimeSpan.FromSeconds(2)) destinations = false;
                bool decision = r.Text?.Cards.Any(c => c.Meaning is var t
                    && (t.Contains("consume") || t.Contains("replace") || t.Contains("hunt") || t.Contains("instigate") || t.Contains("bestow") || t == "end" || t.EndsWith(" end"))) == true;
                // A day begins with its move: decision cards on a new day the log saw no move of yet are the last day's
                // going away (2026-11-01: End hinted for half a second on 11 of 231 new days).
                if (decision && live is { Paged: false, Moved: false } today && today.DayStartedAt > today.MovedAt
                    && _lastBoardAt > today.DayStartedAt && _lastBoardAt - today.DayStartedAt < TimeSpan.FromSeconds(2)) decision = false;
                // The target pick: its banner read, or the log's red-screen sound with nothing else on screen (the
                // banner's text doesn't always read).
                bool consumeOnly = r.Text?.ConsumePick == true || live is { ConsumePick: true } && !destinations && !decision;
                // The Hunt card's target pick reads "Select an Objective" like the Sewer Ratway's: after the Hunt card, on
                // the same day and cell, it's the Hunt's. It dims the cells it doesn't offer (an Ultimate read Normal there),
                // so it plays on from the Sandbox and cards read with the Hunt card (2026-10-28: a Sewer Ratway was hinted).
                if (r.Text?.Cards.Any(c => c.Meaning.Contains("hunt")) == true) _huntOffer = (_sandbox.Warden, _hintSandbox.Days, _hintSandbox.Clone(), r.Text);
                bool huntPick = r.Text?.RatwayPick == true && _huntOffer is { } hunt && hunt.Warden == _sandbox.Warden && hunt.Days == _hintSandbox.Days;
                // The Hunt made: the game starts the next day while the pick's banner still shows; nothing to hint there.
                if (r.Text?.RatwayPick == true && _huntOffer is { } made && made.Warden == _sandbox.Warden && made.Days - 1 == _hintSandbox.Days)
                {
                    if (!ahead)
                    {
                        _hints.Clear();
                        ClearHint();
                    }
                    return;
                }
                // The Sewer Ratway's pick: any cell to go to.
                if (r.Text?.RatwayPick == true) decision = true;
                // Right after a move the Warden's door is still opening: no decision is readable until an Inmate shows.
                // An End card alone is read all the same: the game left it to click when a merge took the Warden's
                // Inmate away (it ends such a day by itself otherwise).
                var here = _sandbox.Warden > 1 ? _sandbox[_sandbox.Warden] : null;
                bool endOnly = r.Text?.Cards is { Count: > 0 } cards && cards.All(c => c.Meaning is "end" || c.Meaning.EndsWith(" end"));
                if (decision && !consumeOnly && r.Text?.RatwayPick != true && here != null && here.Kind != CellKind.Inmate && !endOnly) decision = false;
                // Replace clicked: its card is gone but the old Inmate is still in the Warden's cell; the day read as
                // after the Replace would be the old Inmate's (an End). Wait for the new one, two seconds at most (it can
                // come out the same); a log with clicks tells when it showed (its reveal's sound), and a frame a moment
                // after that is the new one, alike or not.
                bool replaceCard = r.Text?.Cards.Any(c => c.Meaning.Contains("replace")) == true;
                if (replaceCard && here is { Kind: CellKind.Inmate }) _replaceOffer = (_sandbox.Warden, here.Code, here.Rarity, DateTime.Now);
                else if (decision && !consumeOnly && _replaceOffer is { } offer && offer.Warden == _sandbox.Warden && here is { Kind: CellKind.Inmate }
                    && here.Code == offer.Code && here.Rarity == offer.Rarity && DateTime.Now - offer.At < TimeSpan.FromSeconds(2)
                    && !(live is { } replaced && replaced.ReplacedAt > offer.At - TimeSpan.FromSeconds(1) && _lastBoardAt >= replaced.ReplacedAt + ReplaceReveal))
                    decision = false;
                if (!consumeOnly && !destinations && !decision)
                {
                    // The Hunt card clicked: its target pick takes ~2.2 s to come, the cards gone meanwhile. The hint stays,
                    // its target framed dashed, for the click to come (2026-11-01: it went away for those two seconds).
                    bool huntComing = _hint?.Ranked is { Count: > 0 } shown && shown[0].Action.Kind == Tartarus.Rules.ActionKind.Hunt
                        && live is { CardTold: false } clicked && clicked.CardClickedAt > _hintShownAt && DateTime.Now - clicked.CardClickedAt < TimeSpan.FromSeconds(4);
                    if (!ahead && !huntComing)
                    {
                        _hints.Clear();
                        ClearHint();
                    }
                    return;
                }
                // A chain's end: the Warden's Inmate reached Chromatic and goes, or has gone, and the red fades before its
                // Treasure comes. Nothing to decide there (2026-11-02: "Cancel, End" hinted for a second at each).
                if (consumeOnly && (here is { Kind: not CellKind.Inmate, Covered: false } || live is { } risen && DateTime.Now - risen.ChromaticAt < ChromaticGoing))
                {
                    if (!ahead)
                    {
                        _hints.Clear();
                        ClearHint();
                    }
                    return;
                }
                // The red screen spoils the panels' text too (the Objective): the last text read before it stands in.
                if (!consumeOnly && !huntPick) (_textBeforeRed, _daysBeforeRed) = (r.Text, _hintSandbox.Days);
                else _run.ConsumeSeen(_daysBeforeRed);
                if (huntPick) _hintSandbox = _huntOffer!.Value.Board.Clone();
                var text = consumeOnly ? _textBeforeRed ?? r.Text : huntPick ? _huntOffer!.Value.Text : r.Text;
                var observation = Tartarus.Vision.GameObservation.From(_hintSandbox, text, _rerolls);
                _run.Objective(observation, settled: !ahead);
                // The log knows whether today's Consume is used, which a missing Consume card doesn't (no target either).
                if (live is { Paged: false, MaxDays: > 0 } && live.RemainingDays == _hintSandbox.Days)
                {
                    observation.ConsumedToday = live.ConsumedToday;
                    // Consumed today and offered to eat (the red screen, or a Consume card): that's the chain going on.
                    bool consumeCard = r.Text?.Cards.Any(c => c.Meaning.Contains("consume")) == true;
                    observation.ChainGoesOn = live.ConsumedToday && (consumeOnly || consumeCard);
                }
                // A log of pages only, or none, doesn't tell: the target pick seen on this day does (2026-11-01: a missing
                // card taken for a used Consume made a Replace worth less, and End was hinted on 34 of a player's 79 arrivals).
                else if (live is not { Paged: false } && _hintSandbox.Days is { } dayHere) observation.ConsumedToday = _run.ConsumedOn(dayHere);
                if (live is { FromStart: true, ObjectiveDone: true }) observation.ObjectiveDone = true;
                // Survival of the Fittest counts every bite, chain bites too: over the red screen the panel is the one
                // read before it, while the log counts on.
                // Undercurrents the same with the Prison Incidents triggered.
                if (live is { FromStart: true } && observation.Objective is { } objective
                    && (objective == Tartarus.Rules.ObjectiveKind.SurvivalOfTheFittest ? live.Consumes
                        : objective == Tartarus.Rules.ObjectiveKind.Undercurrents ? live.Incidents : -1) is int counted and >= 0)
                    observation.ObjectiveProgress = Math.Max(observation.ObjectiveProgress, Math.Min(counted, Tartarus.Rules.GameData.Target(objective)));
                if (ahead)
                {
                    _hints.Request(observation, consumeOnly, huntPick, ahead: true);
                    return;
                }
                _hinted = _settleRead?.Clone();
                if (_hints.Request(observation, consumeOnly, huntPick)) ClearHint();
            }
            else if (r.Pick is { } pick)
            {
                // An instrument pick after the last day (or before any Sandbox) is a new run's first.
                var under = _hintSandbox;
                Vision.Ocr.ScreenText? startText = null;
                if (pick.Kind == PickRead.Instrument && (under == null || under.Days == 0 || _logRun?.Day == 0))
                {
                    under = EditableSandbox.Empty();
                    under.Theme = _run.Theme;
                    // The Sandbox shows the Objective a moment before the cards cover it: read in this run, it's this run's.
                    if (_hintTextRun is { FromStart: true } && _hintTextRun == _logRun) startText = _hintText;
                }
                // Re-rolls left: the counter's, else the last one read in this run less those the log saw clicked since
                // (its dim text doesn't read on every frame), else none: a run may have no Re-roll at all (Cage Fight).
                if (pick.Kind == PickRead.Instrument)
                {
                    string cards = string.Join(",", pick.Cards.Select(c => c.Id));
                    // The last Re-roll takes its counter away, and a log of pages has no clicks: other cards on the same
                    // pick are a Re-roll made (2026-10-30, a player's report: Re-roll hinted with none left).
                    int Used((LoggedRun Run, int Left, int Used, int Pick, string Cards) seen) =>
                        Math.Max(seen.Run.RerollsUsed - seen.Used,
                            seen.Run.InstrumentPicks == seen.Pick && pick.Cards.Count == 3 && pick.Cards.All(c => c.Id != null) && cards != seen.Cards ? 1 : 0);
                    int? left = _rerollsRead is { } seen && seen.Run == _logRun ? Math.Max(0, seen.Left - Used(seen)) : null;
                    // A counter read after its Re-roll was clicked (the old cards still fading) gives no Re-roll back: a
                    // run's only go down (2026-11-01: "Re-roll 1" read again after the click, and the Objective's pick
                    // hinted a Re-roll the run no longer had).
                    int? read = pick.Rerolls is int counter && left is int leftNow ? Math.Min(counter, leftNow) : pick.Rerolls;
                    if (read is int now && _logRun != null) _rerollsRead = (_logRun, now, _logRun.RerollsUsed, _logRun.InstrumentPicks, cards);
                    _rerolls = read ?? left ?? 0;
                }
                // The Sandbox changed under the pick since it was last seen (a chain, Chromatics leaving it): the hint is
                // for the old one until the player looks at it (the game's eye button) and it's read again.
                // A pick still on screen after the log's Confirm is going away: the change its Confirm made is no reason to
                // look (2026-10-31: every Warden's Treasure taken flashed the eye button).
                bool boardStale = under == _hintSandbox && LiveLogRun is { } logged2 && logged2.BoardChangedAt > _hintBoardAt
                    && !(logged2 is { PickOpen: false } && logged2.PickTakenAt >= logged2.BoardChangedAt);
                if (boardStale != _eyeHint)
                {
                    _eyeHint = boardStale;
                    if (boardStale) Log("Hint: the Sandbox changed under the pick screen, look at it (the eye button)");
                    RefreshOverlay();
                }
                // A card read whole a moment ago that doesn't read now (the one clicked grows and its name shrinks): the
                // hint for the whole pick stays, not one for the cards left (2026-10-30: a weaker instrument hinted for
                // 4 s after the best one was clicked).
                var known = pick.Cards.Where(c => c.Id != null).Select(c => c.Id!).ToList();
                // A Re-roll clicked (the log): the cards on screen go a moment later, and a hint on them would flash
                // (2026-11-01: one of the old three hinted after the click). None until the new offer comes (RerolledCards).
                if (_logRun is { } rerun && (_rerollsSeen is not { } seenRun || seenRun.Run != rerun || seenRun.Used != rerun.RerollsUsed))
                {
                    if (_rerollsSeen is { } was && was.Run == rerun && rerun.RerollsUsed > was.Used && _lastWholePick is { } before) _rerolledCards.Clicked(before.Ids, DateTime.UtcNow);
                    _rerollsSeen = (rerun, rerun.RerollsUsed);
                }
                bool rerolling = _rerolledCards.StillGoing(known, pick.Cards.Count, DateTime.UtcNow);
                bool cardLost = known.Count < pick.Cards.Count && _lastWholePick is { } whole && whole.Kind == pick.Kind && known.All(whole.Ids.Contains);
                if (known.Count == pick.Cards.Count) _lastWholePick = (pick.Kind, known);
                // No pick hint on an old Sandbox: the eye button first.
                if (boardStale || rerolling)
                {
                    _hints.Clear();
                    ClearHint();
                }
                else if (cardLost) { }
                else if (under != null)
                {
                    var text = under == _hintSandbox ? _hintText : startText;
                    // The last scan's own reading of the cells: the change that brought the pick (a door opened on the
                    // Objective's Inmate) is in it, not yet in the steadied Sandbox.
                    if (under == _hintSandbox && _hintRead != null)
                    {
                        under = under.Clone();
                        under.TakeCells(_hintRead);
                    }
                    // The Chromatic that brought this Warden's Treasure (or one more, gone by unheard): the log counts on
                    // while the panel is out of sight.
                    if (LiveLogRun is { FromStart: true } counted && counted.ChromaticAtLeast > under.Chromatic)
                    {
                        under = under.Clone();
                        under.Chromatic = counted.ChromaticAtLeast;
                    }
                    // The Objective's pick right on the move that did it, no reading of the landing between (the cards
                    // came first): the Sandbox under it is the morning's, Destinations and all. The Warden is on the one
                    // that could do the Objective (2026-11-01: the pick was searched with the Warden still at 11 and a
                    // move left to make, and its Prison Incident put on cell 11).
                    if (pick.Kind == PickRead.Instrument && under.Phase == Phases.Destination && under.Destinations.Count > 0
                        && LiveLogRun is { Moved: true } && LandedFor(under, text) is int landed)
                    {
                        under = under.Clone();
                        (under.Warden, under.Phase) = (landed, Phases.Decision);
                        under.Destinations.Clear();
                    }
                    // Re-rolls left as worked out above: the counter shows a moment after the cards (until then the log
                    // tells), and a Warden's Treasure pick has none of its own (the run's go to later offers).
                    var observation = Tartarus.Vision.GameObservation.FromPick(pick, under, text);
                    // Not the run's first pick: the Objective is this run's.
                    if (!observation.StartOfRun) _run.Objective(observation);
                    if (_rerolls is { } left) observation.Rerolls = left;
                    // The Objective done by the move onto a marker: its incident plays out after this pick (the game's
                    // order). Undercurrents is only ever done so (2026-11-01: the search took the incident as gone, and
                    // the shown chests fell 0.7 on average at that pick).
                    observation.IncidentAfterPick = observation.OfferForObjective
                        && (observation.Objective == Tartarus.Rules.ObjectiveKind.Undercurrents
                            || _markedDestinations is { } marked && marked.Days == under.Days && marked.Cells.Contains(under.Warden));
                    if (_hints.Request(observation, consumeOnly: false)) ClearHint();
                }
            }
            else
            {
                _hints.Clear();
                ClearHint();
                // Neither the Sandbox nor a pick: no eye button to ring either (2026-10-30: rung on in the Hideout for
                // half an hour after the run's last Warden's Treasure).
                ShowEye(false);
            }
        }
        catch (Exception ex)
        {
            Log("Hint: " + ex.Message);
        }
    }

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
    // The last Destinations read (Remaining Days, cells) that held a Prison Incident marker.
    private (int? Days, HashSet<int> Cells)? _markedDestinations;

    /// <summary>
    /// The one Destination of <paramref name="morning"/> whose move does its Objective: a marked one for Undercurrents,
    /// one far enough for Prison Walk. Null when none or more than one could.
    /// </summary>
    private int? LandedFor(EditableSandbox morning, Vision.Ocr.ScreenText? text)
    {
        var before = Tartarus.Vision.GameObservation.From(morning, text);
        var candidates = before.Objective switch
        {
            Tartarus.Rules.ObjectiveKind.Undercurrents when _markedDestinations is { } marked && marked.Days == morning.Days =>
                morning.Destinations.Where(marked.Cells.Contains).ToList(),
            Tartarus.Rules.ObjectiveKind.PrisonWalk => morning.Destinations
                .Where(d => (d - morning.Warden + 16) % 16 >= Tartarus.Rules.GameData.Target(Tartarus.Rules.ObjectiveKind.PrisonWalk) - before.ObjectiveProgress).ToList(),
            _ => [],
        };
        return candidates.Count == 1 ? candidates[0] : null;
    }

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
            bool confirm = _confirmSoon && _scan is { Ok: true };
            bool offBoard = _offBoardSince is { } off && DateTime.Now - off >= OffBoardAfter && !confirm && DateTime.Now >= _burstUntil;
            // Nothing to scan (no window, minimized, editing): no spinning at 0.
            double period = offBoard ? OffBoardScan.TotalSeconds : _settings.AutoScanSeconds;
            if (!offBoard && scanned && (confirm || DateTime.Now < _burstUntil)) period = Math.Min(period, QuickScan.TotalSeconds);
            // A slow scan and its hint rest at least as long as they took: on a slow PC (a Sandbox read in 1.3 s) they ran
            // back to back, and the game had what was left. Not a quick one: the rest held every confirming reading back
            // ~0.1 s on a fast PC (2026-11-01: a new day's hint 0.27 s after its first reading with cards, 0.17 before).
            double busy = started.Elapsed.TotalSeconds;
            double wait = Math.Max(Math.Max(period, scanned ? 0 : 0.2) - busy, scanned && busy > SlowScan ? busy : 0);
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
