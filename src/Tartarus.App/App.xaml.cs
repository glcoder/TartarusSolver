using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Tartarus.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Numbers as "12,800" and "6.4" whatever Windows' language; the interface's own words come from Loc
        // (the language of Windows unless the player chose another).
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        // A new version put in this install's place by itself (Updates.StartInstall), then started there.
        if (e.Args is ["--install-update", _, _, ..])
        {
            Updates.InstallHere(e.Args[1..]);
            Shutdown();
            return;
        }
        // One app at a time: a second start shows the running one at once; with no window of it to show (one going down, or
        // starting) it waits a moment.
        bool shown = false;
        if (!SingleInstance.Take(TimeSpan.Zero) && !(shown = SingleInstance.ShowRunning()) && !SingleInstance.Take(TimeSpan.FromSeconds(3)))
            shown = SingleInstance.ShowRunning();
        if (shown || !SingleInstance.Taken)
        {
            AppLog.Write(shown ? "Started again: the running app's window brought to the front, this one goes"
                : "Started again while another one is starting: this one goes");
            AppLog.Flush();
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnUnhandled;
        if (e.Args.Contains(SoftwareRender))
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            AppLog.Write("Software rendering: the graphics card's failed in the last process");
        }
        // The game first: scans and hints take the CPU it leaves (2026-11-01: on a slow PC the game stalled opening a
        // window while a scan ran every moment).
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            self.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
        }
        catch (Exception) { }
        var settings = AppSettings.Load();
        // The interface's language, before any window is built: XAML reads its text through Loc as it loads.
        Loc.Init(settings.UiLanguage);
        // --debug: the whole window (overlay views, hint quality, labels, dataset collecting, the log); else the player's.
        settings.Debug = e.Args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase) || a.Equals("/debug", StringComparison.OrdinalIgnoreCase));
        settings.Save(); // so settings.json exists to edit (hotkeys etc.)
        if (!settings.Debug) settings.PlayerView();
        BugReports.Init(settings);
        Tartarus.Rules.RunScore.SetWorth(settings.CacheWorth);
        // The app is going down: only the stack and the version go, there's no asking.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) BugReports.Crash(ex, fatal: true);
            AppLog.Flush(1000);
        };
        ControlWindow? main = null;
        _watchdog = new UiWatchdog(Dispatcher, () => main?.GameLogPath);
        main = new ControlWindow(settings);
        MainWindow = main;
        main.ContentRendered += (_, _) => _watchdog?.Started();
        main.Show();
    }

    private UiWatchdog? _watchdog;
    private readonly long _startedAt = Environment.TickCount64;
    private bool _renderDead;

    /// <summary>UCEERR_RENDERTHREADFAILURE: WPF's render thread is gone, and every window message says so until the process ends.</summary>
    private const int RenderThreadFailed = unchecked((int)0x88980406);

    /// <summary>The restart after it draws without the graphics card: the new process's first overlay placement hung in it too (2026-11-10).</summary>
    private const string SoftwareRender = "--software-render";

    protected override void OnExit(ExitEventArgs e)
    {
        _watchdog?.Dispose();
        AppLog.Flush(2000);
        // Before Exit's handlers: a restart (the interface's language) starts the next app from one of them.
        SingleInstance.Release();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_renderDead) return;
        AppLog.Write($"Error: {e.Exception}");
        if (e.Exception is COMException { HResult: RenderThreadFailed })
        {
            _renderDead = true;
            RestartAfterRenderFailure(e.Exception);
            return;
        }
        // The app goes on: the player may send a report of it, with what was on screen.
        if (MainWindow is ControlWindow control && BugReports.Enabled) control.Report(e.Exception);
        else MessageBox.Show(e.Exception.ToString(), "Tartarus Solver: error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>
    /// No window can draw any more (2026-11-08, a player in a map: 195 of these in 38 s, a report window over the game after
    /// each): the stack goes without asking and a new process takes over. Not when this one just started: a render thread
    /// that dies at every start must not restart for ever.
    /// </summary>
    private void RestartAfterRenderFailure(Exception ex)
    {
        _watchdog?.Dispose();
        BugReports.Crash(ex, fatal: true);
        bool restart = Environment.TickCount64 - _startedAt >= 60_000 && Environment.ProcessPath != null;
        AppLog.Write(restart ? "Render thread failed: starting again" : "Render thread failed soon after the start: closing");
        // Once this one is down: the new one takes the hotkeys.
        if (restart) Exit += (_, _) => System.Diagnostics.Process.Start(Environment.ProcessPath!,
            Environment.GetCommandLineArgs().Skip(1).Where(a => a != SoftwareRender).Append(SoftwareRender));
        Shutdown();
    }
}
