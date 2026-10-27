using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Microsoft.Diagnostics.Runtime;

namespace Tartarus.App;

/// <summary>
/// Watches the UI thread from a thread of its own: when it doesn't answer for <see cref="Limit"/>, its stack goes to the
/// app's log and out as a bug report with the log (a frozen window can't send one, nor take the report hotkey). The
/// stack is read from a snapshot of this process (ClrMD); nothing of another process is looked at. One report per hang.
/// A UI thread that answered or stood idle by the time its stack was read was late, not hung: only the log tells of
/// it (2026-10-27: a PC back from an hour away, idle in GetMessage).
/// </summary>
public sealed class UiWatchdog : IDisposable
{
    private static readonly TimeSpan Check = TimeSpan.FromMilliseconds(500);
    private readonly Dispatcher _dispatcher;
    private readonly int _uiThread = Environment.CurrentManagedThreadId;
    private readonly Func<string?> _gameLogPath;
    private readonly ManualResetEventSlim _stop = new();
    private volatile bool _answered = true;
    private volatile bool _started;

    /// <summary>Created on the UI thread; <paramref name="gameLogPath"/> is read on the watchdog's.</summary>
    public UiWatchdog(Dispatcher dispatcher, Func<string?> gameLogPath)
    {
        _dispatcher = dispatcher;
        _gameLogPath = gameLogPath;
        new Thread(Run) { IsBackground = true, Name = "UI watchdog" }.Start();
    }

    public static TimeSpan Limit { get; } = TimeSpan.FromSeconds(5);
    /// <summary>The limit until <see cref="Started"/>: a cold start may take seconds to load WPF's theme (2026-10-27: 5 s in the overlay's constructor).</summary>
    public static TimeSpan StartLimit { get; } = TimeSpan.FromSeconds(30);

    /// <summary>The main window is on screen: the start is over, <see cref="Limit"/> holds from now on.</summary>
    public void Started() => _started = true;

    private void Run()
    {
        // Counted in checks, not by the clock: a PC waking from sleep has one late answer, not a hang.
        int missed = 0;
        bool reported = false;
        var since = Stopwatch.StartNew();
        while (!_stop.Wait(Check))
        {
            if (_answered)
            {
                if (reported) Write($"UI thread answers again after {since.Elapsed.TotalSeconds:0} s");
                missed = 0;
                reported = false;
                since.Restart();
                _answered = false;
                _dispatcher.BeginInvoke(DispatcherPriority.Send, () => _answered = true);
                continue;
            }
            if (++missed < (int)((_started ? Limit : StartLimit) / Check) || reported || Debugger.IsAttached) continue;
            reported = true;
            Report(since.Elapsed);
        }
    }

    private void Report(TimeSpan hung)
    {
        string stacks;
        try { stacks = Stacks(); }
        catch (Exception ex) { stacks = "stack not read: " + ex.Message; }
        // The UI thread's stack is the first: where it stands (not the runtime's "[InlinedCallFrame]" and the like), and
        // the app's own method that got it there.
        var ui = stacks.Split('\n').Skip(1).TakeWhile(l => l.StartsWith("  ")).Select(l => l.Trim()).Where(l => !l.StartsWith('[')).ToList();
        string top = ui.FirstOrDefault() ?? "unknown";
        if (ui.FirstOrDefault(l => l.StartsWith("Tartarus.")) is { } ours && ours != top) top += " ← " + ours;
        // Idle: waiting for the next message in the dispatcher's loop, nothing of its own above it.
        const string wait = "System.Windows.Threading.Dispatcher.GetMessage(";
        bool idle = ui.Any(l => l.StartsWith(wait)) && ui.TakeWhile(l => !l.StartsWith(wait)).All(l => l.Contains("GetMessage") || l.StartsWith("ILStubClass."));
        if (_answered || idle)
        {
            Write($"UI thread late by {hung.TotalSeconds:0} s, idle by the time its stack was read (not reported)");
            return;
        }
        Write($"UI thread not answering for {hung.TotalSeconds:0} s, at {top}{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", ui)}");
        try
        {
            var report = new BugReport();
            report.AddText("stacks.txt", stacks);
            report.AddText("app.log", BugReports.Tail(AppSettings.LogPath, 3000));
            if (_gameLogPath() is { } log) report.AddText("game_s16.log", BugReports.GameLogRun(log));
            Write("Hang report: " + BugReports.SendHang(report, top, stacks));
        }
        catch (Exception ex)
        {
            Write("Hang report failed: " + ex.Message);
        }
    }

    /// <summary>The managed stacks of every thread, the UI thread's first.</summary>
    private string Stacks()
    {
        using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        using var runtime = target.ClrVersions[0].CreateRuntime();
        var text = new StringBuilder();
        foreach (var thread in runtime.Threads.Where(t => t.IsAlive).OrderBy(t => t.ManagedThreadId != _uiThread))
        {
            var frames = thread.EnumerateStackTrace().Select(f => f.Method?.Signature ?? $"[{f.FrameName ?? f.Kind.ToString()}]").ToList();
            if (frames.Count == 0) continue;
            text.AppendLine($"Thread {thread.ManagedThreadId}{(thread.ManagedThreadId == _uiThread ? " (UI)" : "")}:");
            foreach (var frame in frames) text.AppendLine("  " + frame);
        }
        return text.ToString();
    }

    private static void Write(string message)
    {
        try { File.AppendAllText(AppSettings.LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
        catch (IOException) { }
    }

    public void Dispose() => _stop.Set();
}
