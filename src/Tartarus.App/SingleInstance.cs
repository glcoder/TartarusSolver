using System.IO;
using System.Text.RegularExpressions;
using Tartarus.Capture;

namespace Tartarus.App;

/// <summary>
/// One app per Windows session: a second start brings the running one's window to the front and goes. Two or three at once
/// fought over the hotkeys, the overlay, the log and the statistics (2026-11-06: 14 of 373 installs in reports started 2-3
/// processes within a second, each then reporting the same hang).
/// </summary>
public static class SingleInstance
{
    private const string Name = @"Local\TartarusSolver.Running";
    // The running app's main window ("Tartarus Solver 0.7.44"; dialogs are "Tartarus Solver: ...").
    private static readonly Regex MainTitle = new(@"^Tartarus Solver \d", RegexOptions.CultureInvariant);
    private static Mutex? _mutex;

    /// <summary>This process is the one app.</summary>
    public static bool Taken => _mutex != null;

    /// <summary>
    /// True: this is the one app now. Waits <paramref name="wait"/> for one going down (a restart starts the next one as
    /// it exits). Kept by the UI thread, which takes it.
    /// </summary>
    public static bool Take(TimeSpan wait)
    {
        var mutex = new Mutex(false, Name);
        bool taken;
        try { taken = mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { taken = true; } // the last one crashed holding it
        if (!taken)
        {
            mutex.Dispose();
            return false;
        }
        _mutex = mutex;
        return true;
    }

    /// <summary>Lets the next one start now (a restart, from the UI thread that took it).</summary>
    public static void Release()
    {
        if (_mutex is not { } mutex) return;
        _mutex = null;
        try { mutex.ReleaseMutex(); }
        catch (ApplicationException) { } // not this thread's
        mutex.Dispose();
    }

    /// <summary>The running app's main window, restored if minimized and put in front; false when it has none yet (starting).</summary>
    public static bool ShowRunning()
    {
        string self = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "TartarusSolver");
        var running = Win32Windows.List().FirstOrDefault(w => MainTitle.IsMatch(w.Title) && w.Process.Equals(self, StringComparison.OrdinalIgnoreCase));
        if (running.Hwnd == IntPtr.Zero) return false;
        if (Native.IsIconic(running.Hwnd)) Native.ShowWindow(running.Hwnd, Native.SW_RESTORE);
        // Started by the player just now, so this process may give the foreground away.
        Native.SetForegroundWindow(running.Hwnd);
        return true;
    }
}
