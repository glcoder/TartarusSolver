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
        DispatcherUnhandledException += OnUnhandled;
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

    protected override void OnExit(ExitEventArgs e)
    {
        _watchdog?.Dispose();
        AppLog.Flush(2000);
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        AppLog.Write($"Error: {e.Exception}");
        // The app goes on: the player may send a report of it, with what was on screen.
        if (MainWindow is ControlWindow control && BugReports.Enabled) control.Report(e.Exception);
        else MessageBox.Show(e.Exception.ToString(), "Tartarus Solver: error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
