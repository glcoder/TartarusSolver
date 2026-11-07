using Tartarus.Vision;

namespace Tartarus.App;

/// <summary>The app's log file (<see cref="AppSettings.LogPath"/>), written off the caller's thread.</summary>
public static class AppLog
{
    private static readonly BackgroundLog File = new(AppSettings.LogPath, maxBytes: 20 << 20);

    /// <summary>Queues a line with the time now, to the millisecond: how long a hint takes after the game's click is told by them.</summary>
    public static void Write(string message) => File.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");

    /// <summary>The lines so far in the file: before it's read for a report, or the app goes.</summary>
    public static bool Flush(int timeoutMs = 500) => File.Flush(timeoutMs);
}
