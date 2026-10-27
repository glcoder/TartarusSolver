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

    private WindowCapture? _capture;
    private WindowInfo? _target;

    // What the overlay shows: the last scanned frame, its scan, and its labels (edited by hand in edit mode).
    private Frame? _frame;
    private ScanResult? _scan;
    private EditableSandbox? _scannedSandbox;
    // The last scan as read, before KeepCovered and Steady: what Steady compares the next one with.
    private EditableSandbox? _lastRead;
    private EditableSandbox? _sandbox;
    private int _selected = -1;
    private bool _editMode;
    private bool _savedSinceScan;
    private string? _lastAutoDescription;
    private string? _status;

    public ControlWindow(AppSettings settings)
    {
        _settings = settings;
        _scans = new ScanService(settings.OcrThreads);
        _hints = new HintService(settings.HintPlayouts, settings.HintSeconds);
        _hints.Log += line => Dispatcher.BeginInvoke(() => Log("Hint: " + line));
        _hints.Ready += hint => Dispatcher.BeginInvoke(() =>
        {
            var best = hint.Shown.Count > 0 ? hint.Shown[0] : null;
            Tartarus.Rules.GameAction? before = _hint?.Shown is { Count: > 0 } prev ? prev[0].Action : null;
            if (best != null && before != best.Action)
                Log($"Hint: {SandboxOverlayView.ActionName(best.Action)} worth ≈{best.Mean:0.000} ({best.Chromatic:0.00} Chromatic)"
                    + string.Concat(hint.Ties.Select(t => $", tied with {SandboxOverlayView.ActionName(t.Action)} ≈{t.Mean:0.000}"))
                    + $" ({hint.Result.Elapsed.TotalMilliseconds:0} ms)");
            _hint = hint;
            HintStatus.Text = $"Last hint: {hint.Result.Elapsed.TotalMilliseconds:0} ms, {hint.Result.Playouts:N0} playouts"
                + (_hints.Net != null ? " · value net" : " · no value net") + (_hints.Policy != null ? " · policy net" : "") + (_hints.Picks != null ? " · picks net" : "");
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
        ScanButton.Content = "Scan" + Hk("scan");
        OverlayCheck.Content = "Overlay" + Hk("overlay");
        EditCheck.Content = "Edit mode" + Hk("edit");
        SaveButton.Content = "Save frame" + Hk("save");
        AutoScanCheck.Content = "Auto" + Hk("auto") + ", every";
        ReportButton.Content = "Report a bug" + Hk("report");
        Title = $"Tartarus Solver {AppVersion.Number}" + (settings.Debug ? " (debug)" : "");
        // The player's window: the game window, scanning, the game's log, the version and bug reports.
        if (!settings.Debug)
            foreach (var panel in new FrameworkElement[] { ViewPanel, HintChecks, DebugPanel }) panel.Visibility = Visibility.Collapsed;
        VersionText.Text = "Version " + AppVersion.Short;
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
        _placeTimer.Tick += (_, _) => PlaceOverlay();
        _attachTimer.Tick += (_, _) => TryAutoAttach();

        _overlay.View.MouseLeftButtonUp += Overlay_MouseUp;
        _overlay.View.MouseRightButtonUp += Overlay_MouseUp;

        RefreshWindowList();
        UpdateDatasetStatus();
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
        _scans.Dispose();
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

    /// <summary>
    /// The game's language (the setting's, else the game's own settings file beside its log) to OCR: checked every few
    /// seconds, the player may switch it in the game. A language whose recognizer isn't installed is offered for
    /// download first.
    /// </summary>
    private void CheckGameLanguage()
    {
        if (_askingOcr) return;
        string code = _settings.GameLanguage ?? (_gameLogPath != null ? GameLog.LanguageOf(_gameLogPath) : null) ?? "en";
        if (code == _gameLanguage) return;
        _gameLanguage = code;
        var pack = Tartarus.Vision.Ocr.OcrPacks.ForGame(code);
        bool have = Tartarus.Vision.Ocr.OcrPacks.Locate(pack, Tartarus.Vision.Ocr.OcrEngine.FindModelDir()) != null;
        Log($"Game language: {code}{(_settings.GameLanguage != null ? " (set)" : "")} · OCR {(have ? pack.Id : $"{pack.Id} recognizer not installed")}");
        if (!have && !_declinedOcr.Contains(pack.Id))
        {
            // After the window is up: this runs from its start too.
            Dispatcher.BeginInvoke(() => OfferOcrDownload(pack, code), DispatcherPriority.ApplicationIdle);
            return;
        }
        _scans.SetGameLanguage(code);
    }

    private void OfferOcrDownload(Tartarus.Vision.Ocr.OcrPack pack, string code)
    {
        _askingOcr = true;
        PlaceOverlay();
        try
        {
            var window = new DownloadWindow(pack, code);
            if (IsVisible) window.Owner = this;
            if (window.ShowDialog() != true) _declinedOcr.Add(pack.Id);
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

    private void TryAutoAttach()
    {
        CheckGameLanguage();
        if (_capture != null || AutoAttachCheck.IsChecked != true) return;
        var pattern = new Regex(_settings.WindowTitle);
        var game = Win32Windows.FindGame(pattern) ?? default;
        if (game.Hwnd != IntPtr.Zero && !Win32Windows.IsMinimized(game.Hwnd)) StartCapture(game);
    }

    private void StartCapture(WindowInfo w)
    {
        StopCapture();
        try
        {
            var capture = new WindowCapture(w.Hwnd);
            capture.Closed += () => Dispatcher.BeginInvoke(() =>
            {
                if (_capture != capture) return;
                Log($"Window \"{w.Title}\" closed");
                StopCapture();
            });
            _capture = capture;
            _target = w;
            CaptureButton.Content = "Stop ■";
            CaptureStatus.Text = $"Capturing: {w}";
            Log($"Capturing window \"{w.Title}\" ({w.Process}), {Win32Windows.VisibleBounds(w.Hwnd)}");
            // The log of the very install that's running, from its executable's path.
            if (Win32Windows.ImagePath(w.Pid) is { } exe) StartGameLog(exe);
        }
        catch (Exception ex)
        {
            Log($"Could not start capturing \"{w.Title}\": {ex.Message}");
        }
    }

    private void StopCapture()
    {
        _capture?.Dispose();
        _capture = null;
        _target = null;
        CaptureButton.Content = "Capture ▶";
        CaptureStatus.Text = "Not capturing";
        if (_overlay.IsVisible) _overlay.Hide();
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
        if (!_overlay.IsVisible) _overlay.Show();
        _overlay.PlaceOver(bounds);

        // A scan of a differently sized frame no longer lines up with the window.
        bool stale = _frame != null && (_frame.Width != bounds.Width || _frame.Height != bounds.Height);
        if (stale != _stale)
        {
            _stale = stale;
            RefreshOverlay();
        }
    }

    private bool _stale;
    private readonly RunMemory _run = new();
    private readonly HintService _hints;
    private Hint? _hint;
    // The last Sandbox the hints saw (with the run's memory) and its text: a pick screen's hint plays on from it.
    private EditableSandbox? _hintSandbox;
    private Vision.Ocr.ScreenText? _hintText;
    // The log's run that text was read in.
    private LoggedRun? _hintTextRun;
    // The same scan as read, before Steady: the base of a pick that comes up right after a change.
    private EditableSandbox? _hintRead;
    private GameLogWatcher? _gameLog;
    private string? _gameLogPath;
    private readonly PickVotes _pickVotes = new();
    private readonly Tartarus.Rules.Engine _rulesEngine = new();
    /// <summary>When the Sandbox was last seen (a frame's capture time), and the pick screen's request to look at it again.</summary>
    private DateTime _lastBoardAt;
    // The hint is the eye button: the Sandbox changed under a pick (look at it), or it was read while the pick waits (go back).
    private bool _eyeHint;
    // The Warden's Inmate while the Replace card was offered: after the click the card leaves a moment before the new
    // Inmate shows.
    private (int Warden, Tartarus.Vision.Code? Code, Tartarus.Vision.Rarity Rarity, DateTime At)? _replaceOffer;

    /// <summary>The run going on, as the game's log tells it; null without a log or between runs.</summary>
    private LoggedRun? LiveLogRun => _logRun is { Finished: false, MaxDays: > 0 } run ? run : null;
    private LoggedRun? _logRun;
    private bool _logRunFinished;
    // Re-rolls the run has left, as the last instrument pick showed them (shared by every pick of the run).
    private int? _rerolls;
    // The last counter read: in which run, how many it showed, and the Re-rolls the log had seen clicked by then.
    private (LoggedRun Run, int Left, int Used)? _rerollsRead;
    // The last Sandbox read before the Consume target pick's red screen.
    private Vision.Ocr.ScreenText? _textBeforeRed;

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
            Status = stale ? "Tartarus: the window's size changed, a new scan is needed" : _status,
            EyeHint = !stale && _settings.Hints && _eyeHint,
            HintsOnly = _settings.Hints && _settings.HintsOnly,
        };
    }

    // ---------- Scanning ----------

    private async Task<bool> ScanAsync(bool manual)
    {
        if (_capture == null)
        {
            if (manual) ScanStatus.Text = "Not capturing a window";
            return false;
        }
        if (_scans.Busy || (_editMode && !manual)) return false;
        if (_target is { } t && Win32Windows.IsMinimized(t.Hwnd)) return false;
        var frame = _capture.Grab();
        if (frame == null)
        {
            if (manual) ScanStatus.Text = "No frames yet (is the window minimized?)";
            return false;
        }
        ScanResult r;
        try
        {
            r = await _scans.ScanAsync(frame);
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
        if (previous != null)
        {
            _scannedSandbox.KeepCovered(previous);
            if (_lastRead != null) _scannedSandbox.Steady(previous, _lastRead);
        }
        _lastRead = read;
        if (r.Ok && WholeBoard(_scannedSandbox)) _lastBoardAt = frame.Timestamp.LocalDateTime;
        // The Consume target pick: the Sandbox stands still under the flickering red, so each Inmate shows its most
        // frequent reading over the pick's frames.
        bool targetPick = r.Ok && (r.Text?.ConsumePick == true || LiveLogRun is { ConsumePick: true } && r.Text?.Cards.Count == 0);
        if (targetPick) _pickVotes.Apply(_scannedSandbox);
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
            if (_logRun is { Finished: false } logged) KeepTheme(logged, theme);
        }
        _sandbox = _scannedSandbox.Clone();
        _selected = -1;
        _savedSinceScan = false;
        _status = r.Ok ? null : manual || _editMode ? $"Tartarus: no Sandbox found ({r.TotalMs:0} ms)" : null;
        RefreshOverlay();
        RefreshLabelsUi();
        ScanStatus.Text = (r.Ok ? $"Sandbox · {r.TotalMs:0} ms" : $"No Sandbox: {r.Reason} · {r.TotalMs:0} ms")
            + (r.Timings.TryGetValue("ocr", out var ocrMs) ? $" (OCR {ocrMs:0}, cells {r.Timings.GetValueOrDefault("cells"):0})" : "")
            + (r.Timings.ContainsKey("presence") ? $" · presence {r.Loc?.Score:0.00}" : "")
            + $" · {frame.Width}×{frame.Height} · {r.Source}";
        string description = SandboxText.Describe(r);
        // What was read, without the scan's timing and presence score (they differ every scan).
        string reading = r.Ok ? SandboxText.Describe(_scannedSandbox) : description;
        if (manual || reading != _lastAutoDescription) Log((manual ? "Scan: " : "Changed: ") + description.Replace(Environment.NewLine, " | ").Replace("\n", " | "));
        _lastAutoDescription = reading;
        if (!manual) AutoCollect(frame, r);
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
        HintLevelText.Text = $"~{level.Milliseconds:0.#} ms per move · ≈{level.Chromatic:0.0} Chromatic per run ({level.Playouts:N0} playouts per move)";
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
            // With a log it tells runs apart, and the day count's reading no longer has to; without, recognition alone.
            _run.NewRunByDays = path == null;
            if (path != null)
            {
                var watcher = _gameLog = new GameLogWatcher(path);
                watcher.Changed += run => Dispatcher.BeginInvoke(() =>
                {
                    if (_gameLog == watcher) OnLogRun(run);
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
        if (_run.Theme != null || _themeChecked is { } c && c.Run == run && c.MaxDays == run.MaxDays) return;
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

    private void ShowLogStatus() => LogStatus.Text =
        (_gameLogPath ?? "not found: screen recognition only") + (_settings.GameLogPath != null ? " (chosen by hand)" : " (found automatically)");

    private void PickLogButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "UE_game.log: game folder\\UE_game\\TorchLight\\Saved\\Logs",
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
        _run.Days(run.RemainingDays);
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
        if (run.Finished && !_logRunFinished)
        {
            _logRunFinished = true;
            Log($"Log: run finished — Chromatic {run.Chromatic}, Prison Incidents {run.Incidents}, Consume {run.Consumes}");
        }
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

    /// <summary>A Sandbox read whole: the Warden found and no more than a couple of cells under the interface.</summary>
    private static bool WholeBoard(EditableSandbox b) => b.Warden > 0 && b.Cells.Count(c => c.Covered) < 3;

    private void RequestHint(ScanResult r)
    {
        if (!_settings.Hints || _editMode) return;
        try
        {
            // A Sandbox half hidden (a Chromatic's flight or a popup over it, the Warden not found): no position to
            // search, nor a base for the pick that may follow; the last whole one stays.
            if (r.Ok && _sandbox != null && !WholeBoard(_sandbox)) return;
            if (r.Ok && _sandbox != null)
            {
                _hintSandbox = _sandbox.Clone();
                _hintRead = _lastRead?.Clone();
                _hintText = r.Text;
                _hintTextRun = _logRun;
                // The log knows the day: it stands in when OCR didn't read the counter or read it wrong. The screen
                // shows a new day about a second before the log has it, so one day fewer than the log's is the screen
                // being ahead, not a misread. A pick's hint plays on from this Sandbox too (a popup darkens the counter).
                var live = LiveLogRun;
                if (live != null && (_hintSandbox.Days is not int d || (d != live.RemainingDays && d != live.RemainingDays - 1)))
                    _hintSandbox.Days = live.RemainingDays;
                // A run the log saw from its start: its Chromatic count, or the panel's when higher (a sound can go missing).
                if (live is { FromStart: true }) _hintSandbox.Chromatic = Math.Max(_hintSandbox.Chromatic, live.ChromaticAtLeast);
                // The Sandbox viewed from a pick (the eye button): read now, back to the pick is the hint.
                bool viewing = LiveLogRun is { PickOpen: true };
                if (viewing != _eyeHint)
                {
                    _eyeHint = viewing;
                    if (viewing) Log("Hint: the Sandbox is read, back to the pick (the eye button)");
                    Dispatcher.BeginInvoke(RefreshOverlay);
                }
                if (viewing)
                {
                    _hints.Clear();
                    ClearHint();
                    return;
                }
                // Only a screen that says what it asks: the Consume target pick's banner, Destination cards read, or a
                // decision card. Frames in between (cards sliding in, a day's banner) would be taken for the wrong phase.
                bool destinations = _sandbox.Phase == Phases.Destination && _sandbox.Destinations.Count > 0;
                bool decision = r.Text?.Cards.Any(c => c.Meaning is var t
                    && (t.Contains("consume") || t.Contains("replace") || t.Contains("hunt") || t.Contains("instigate") || t.Contains("bestow") || t == "end" || t.EndsWith(" end"))) == true;
                // The target pick: its banner read, or the log's red-screen sound with nothing else on screen (the
                // banner's text doesn't always read).
                bool consumeOnly = r.Text?.ConsumePick == true || live is { ConsumePick: true } && !destinations && !decision;
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
                // come out the same).
                bool replaceCard = r.Text?.Cards.Any(c => c.Meaning.Contains("replace")) == true;
                if (replaceCard && here is { Kind: CellKind.Inmate }) _replaceOffer = (_sandbox.Warden, here.Code, here.Rarity, DateTime.Now);
                else if (decision && !consumeOnly && _replaceOffer is { } offer && offer.Warden == _sandbox.Warden && here is { Kind: CellKind.Inmate }
                    && here.Code == offer.Code && here.Rarity == offer.Rarity && DateTime.Now - offer.At < TimeSpan.FromSeconds(2))
                    decision = false;
                if (!consumeOnly && !destinations && !decision)
                {
                    _hints.Clear();
                    ClearHint();
                    return;
                }
                // The red screen spoils the panels' text too (the Objective): the last text read before it stands in.
                if (!consumeOnly) _textBeforeRed = r.Text;
                var text = consumeOnly ? _textBeforeRed ?? r.Text : r.Text;
                var observation = Tartarus.Vision.GameObservation.From(_hintSandbox, text, _rerolls);
                // The log knows whether today's Consume is used, which a missing Consume card doesn't (no target either).
                if (live != null && live.RemainingDays == _hintSandbox.Days)
                {
                    observation.ConsumedToday = live.ConsumedToday;
                    // Consumed today and offered to eat (the red screen, or a Consume card): that's the chain going on.
                    bool consumeCard = r.Text?.Cards.Any(c => c.Meaning.Contains("consume")) == true;
                    observation.ChainGoesOn = live.ConsumedToday && (consumeOnly || consumeCard);
                }
                if (live is { FromStart: true, ObjectiveDone: true }) observation.ObjectiveDone = true;
                // Survival of the Fittest counts every bite, chain bites too: over the red screen the panel is the one
                // read before it, while the log counts on.
                // Undercurrents the same with the Prison Incidents triggered.
                if (live is { FromStart: true } && observation.Objective is { } objective
                    && (objective == Tartarus.Rules.ObjectiveKind.SurvivalOfTheFittest ? live.Consumes
                        : objective == Tartarus.Rules.ObjectiveKind.Undercurrents ? live.Incidents : -1) is int counted and >= 0)
                    observation.ObjectiveProgress = Math.Max(observation.ObjectiveProgress, Math.Min(counted, Tartarus.Rules.GameData.Target(objective)));
                if (_hints.Request(observation, consumeOnly)) ClearHint();
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
                    if (pick.Rerolls is int read && _logRun != null) _rerollsRead = (_logRun, read, _logRun.RerollsUsed);
                    _rerolls = pick.Rerolls ?? (_rerollsRead is { } seen && seen.Run == _logRun ? Math.Max(0, seen.Left - (seen.Run.RerollsUsed - seen.Used)) : 0);
                }
                // The Sandbox changed under the pick since it was last seen (a chain, Chromatics leaving it): the hint is
                // for the old one until the player looks at it (the game's eye button) and it's read again.
                bool boardStale = under == _hintSandbox && LiveLogRun is { } logged2 && logged2.BoardChangedAt > _lastBoardAt;
                if (boardStale != _eyeHint)
                {
                    _eyeHint = boardStale;
                    if (boardStale) Log("Hint: the Sandbox changed under the pick screen, look at it (the eye button)");
                    RefreshOverlay();
                }
                // No pick hint on an old Sandbox: the eye button first.
                if (boardStale)
                {
                    _hints.Clear();
                    ClearHint();
                }
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
                    // Re-rolls left as worked out above: the counter shows a moment after the cards (until then the log
                    // tells), and a Warden's Treasure pick has none of its own (the run's go to later offers).
                    var observation = Tartarus.Vision.GameObservation.FromPick(pick, under, text);
                    if (_rerolls is { } left) observation.Rerolls = left;
                    if (_hints.Request(observation, consumeOnly: false)) ClearHint();
                }
            }
            else
            {
                _hints.Clear();
                ClearHint();
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
        if (pick.Kind == PickRead.Treasure && r.Layout is { } layout)
            _ = Task.Run(() => WardenTreasures.SaveMissingIcons(frame, layout, pick)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
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
    /// Auto scan: a frame every <see cref="AppSettings.AutoScanSeconds"/> from the last one's start, never before the last
    /// one's reading and hint are done (at 0, as soon as they are).
    /// </summary>
    private async Task AutoScanLoopAsync()
    {
        int loop = ++_autoScanLoop;
        while (loop == _autoScanLoop)
        {
            var started = Stopwatch.StartNew();
            bool scanned;
            try
            {
                scanned = await ScanAsync(manual: false);
                await _hints.Searching;
            }
            catch (Exception ex)
            {
                // As the app's own errors: logged, offered as a report; the loop stops (no error a frame).
                Log("Auto scan stopped: " + ex);
                AutoScanCheck.IsChecked = false;
                if (BugReports.Enabled) Report(ex);
                return;
            }
            // Nothing to scan (no window, minimized, editing): no spinning at 0.
            double wait = Math.Max(_settings.AutoScanSeconds, scanned ? 0 : 0.2) - started.Elapsed.TotalSeconds;
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(wait, 0.001)));
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
        menu.Items.Add(new MenuItem { Header = $"Cell {n}: {SandboxText.CellShort(cell)}", IsEnabled = false });
        menu.Items.Add(new Separator());

        if (idx > 0)
        {
            menu.Items.Add(Item("Opened, empty", cell.Kind == CellKind.Empty, () => SetKind(cell, CellKind.Empty)));
            menu.Items.Add(Item("Unopened", cell.Kind == CellKind.Unopened, () => SetKind(cell, CellKind.Unopened)));
            menu.Items.Add(new Separator());
            // An Inmate: its Code, then its rarity (Normal .. Ultimate: Chromatic leaves the Sandbox).
            foreach (var code in Codes.All)
            {
                var sub = new MenuItem { Header = Codes.Name(code), IsChecked = cell.Kind == CellKind.Inmate && cell.Code == code };
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
            var visibleMenu = new MenuItem { Header = "Visible rarity (Unopened)", IsChecked = cell.VisibleRarity != null };
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
        menu.Items.Add(Item(sandbox.Destinations.Contains(n) ? $"Remove from Destinations (D{sandbox.Destinations.IndexOf(n) + 1})" : $"Add to Destinations (D{sandbox.Destinations.Count + 1})", sandbox.Destinations.Contains(n), () =>
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
            var mi = new MenuItem { Header = header, IsChecked = isChecked };
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
        var (frame, scan, sandbox, title) = (_frame, _scan, _sandbox.Clone(), _target?.Title);
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
        string line = $"{DateTime.Now:HH:mm:ss} {message}";
        try { File.AppendAllText(AppSettings.LogPath, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}"); }
        catch { }
        LogList.Items.Add(line);
        while (LogList.Items.Count > 200) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
