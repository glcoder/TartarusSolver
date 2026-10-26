using System.Windows;
using System.Windows.Threading;

namespace Tartarus.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The interface is in English (localization comes later): numbers as "12,800" and "6.4" whatever Windows' language.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        DispatcherUnhandledException += OnUnhandled;
        var settings = AppSettings.Load();
        settings.Save(); // so settings.json exists to edit (hotkeys etc.)
        BugReports.Init(settings);
        Tartarus.Rules.RunScore.SetWorth(settings.CacheWorth);
        // The app is going down: only the stack and the version go, there's no asking.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) BugReports.Crash(ex, fatal: true);
        };
        var main = new ControlWindow(settings);
        MainWindow = main;
        main.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try { System.IO.File.AppendAllText(AppSettings.LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Error: {e.Exception}{Environment.NewLine}"); }
        catch { }
        // The app goes on: the player may send a report of it, with what was on screen.
        if (MainWindow is ControlWindow control && BugReports.Enabled) control.Report(e.Exception);
        else MessageBox.Show(e.Exception.ToString(), "Tartarus Solver: error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
