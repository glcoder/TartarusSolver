using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Sentry;

namespace Tartarus.App;

/// <summary>A bug report's files, gathered the moment the player asked for it; the comment comes later.</summary>
public sealed class BugReport
{
    public DateTimeOffset At { get; } = DateTimeOffset.Now;
    /// <summary>Path in the zip to its contents.</summary>
    public Dictionary<string, byte[]> Files { get; } = [];
    /// <summary>The frame with the overlay on it, sent apart too so it shows in the report at once.</summary>
    public byte[]? Screenshot { get; set; }
    /// <summary>The exception that brought the report up, when the app caught one.</summary>
    public Exception? Crash { get; set; }

    public void AddText(string path, string text) => Files[path] = Encoding.UTF8.GetBytes(BugReports.Scrub(text));

    public byte[] Zip(string comment)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, byte[] data)
            {
                // Images are compressed already.
                var level = path.EndsWith(".png") || path.EndsWith(".jpg") ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                using var s = zip.CreateEntry(path, level).Open();
                s.Write(data);
            }
            Add("comment.txt", Encoding.UTF8.GetBytes(BugReports.Scrub(comment)));
            foreach (var (path, data) in Files) Add(path, data);
        }
        return buffer.ToArray();
    }
}

/// <summary>
/// Sends bug reports and crashes to Sentry (when a DSN is set: settings.json's ReportDsn, else <see cref="DefaultDsn"/>),
/// and keeps each report's zip in %LOCALAPPDATA%\TartarusSolver\reports. Nothing names the player: no machine or user
/// name, no IP, paths under the user's folder cut to %USERPROFILE%; a random id per install tells testers apart.
/// </summary>
public static partial class BugReports
{
    /// <summary>
    /// The DSN built in: the build's SentryDsn property (or the TARTARUS_SENTRY_DSN environment variable), which
    /// tools/package.py passes; none in a plain build, so a fork doesn't report to someone else's Sentry.
    /// </summary>
    public static string DefaultDsn { get; } = typeof(BugReports).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "SentryDsn")?.Value ?? "";

    public static string Folder => Path.Combine(AppSettings.Folder, "reports");

    public static bool Enabled { get; private set; }

    public static void Init(AppSettings settings)
    {
        string dsn = string.IsNullOrWhiteSpace(settings.ReportDsn) ? DefaultDsn : settings.ReportDsn;
        if (string.IsNullOrWhiteSpace(dsn)) return;
        try
        {
            SentrySdk.Init(o =>
            {
                o.Dsn = dsn;
                o.Release = "tartarus-solver@" + AppVersion.Number;
                o.SendDefaultPii = false;
                o.ServerName = "";
                o.AutoSessionTracking = false;
                o.IsGlobalModeEnabled = true;
                o.MaxAttachmentSize = 20 * 1024 * 1024;
                o.SetBeforeSend(e =>
                {
                    if (e.SentryExceptions != null)
                        foreach (var ex in e.SentryExceptions) ex.Value = ex.Value is { } v ? Scrub(v) : null;
                    return e;
                });
            });
            SentrySdk.ConfigureScope(scope =>
            {
                scope.User = new SentryUser { Id = settings.InstallId };
                foreach (var m in AppVersion.Models) scope.SetTag("model." + m.Name, m.Hash);
            });
            Enabled = true;
        }
        catch (Exception) { }
    }

    /// <summary>Sends the report (when Sentry is set up) and keeps its zip; returns what to tell the player.</summary>
    public static async Task<string> SendAsync(BugReport report, string comment)
    {
        byte[] zip = report.Zip(comment);
        string saved = Keep(report, zip);
        if (!Enabled) return $"Bug report saved (sending not set up): {saved}";
        try
        {
            string title = comment.Trim() is { Length: > 0 } c ? (c.Length > 80 ? c[..80] + "…" : c) : "no comment";
            void Fill(Scope scope)
            {
                scope.SetTag("kind", report.Crash != null ? "crash-report" : "report");
                scope.SetExtra("comment", Scrub(comment));
                scope.SetExtra("version", AppVersion.Full);
                scope.AddAttachment(zip, $"report-{report.At:yyyyMMdd_HHmmss}.zip", AttachmentType.Default, "application/zip");
                if (report.Screenshot is { } shot) scope.AddAttachment(shot, "screenshot.jpg", AttachmentType.Default, "image/jpeg");
            }
            var id = report.Crash is { } crash
                ? SentrySdk.CaptureException(crash, Fill)
                : SentrySdk.CaptureMessage("Report: " + Scrub(title), Fill, SentryLevel.Info);
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(60));
            return $"Bug report sent ({id.ToString()[..8]}), copy: {saved}";
        }
        catch (Exception ex)
        {
            return $"Bug report not sent ({ex.Message}), saved: {saved}";
        }
    }

    /// <summary>An exception with nothing but its stack and the version; <paramref name="fatal"/>: the app is going down, sent before it does.</summary>
    public static void Crash(Exception ex, bool fatal = false)
    {
        if (!Enabled) return;
        SentrySdk.CaptureException(ex, scope => scope.SetTag("kind", fatal ? "fatal" : "crash"));
        if (fatal) SentrySdk.Flush(TimeSpan.FromSeconds(5));
    }

    /// <summary>The zip in reports/, the last 20 kept.</summary>
    private static string Keep(BugReport report, byte[] zip)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, $"report-{report.At:yyyyMMdd_HHmmss}.zip");
        File.WriteAllBytes(path, zip);
        foreach (var old in new DirectoryInfo(Folder).GetFiles("report-*.zip").OrderByDescending(f => f.Name).Skip(20)) old.Delete();
        return path;
    }

    /// <summary>The user's folder in paths as %USERPROFILE%, and the user's and machine's names out.</summary>
    public static string Scrub(string text)
    {
        text = UserFolderRx().Replace(text, "%USERPROFILE%");
        foreach (var name in new[] { Environment.UserName, Environment.MachineName })
            if (name.Length >= 3) text = Regex.Replace(text, $@"\b{Regex.Escape(name)}\b", "<user>", RegexOptions.IgnoreCase);
        return text;
    }

    [GeneratedRegex(@"(?i)[a-z]:[\\/]+users[\\/]+[^\\/:*?""<>|\r\n]+")]
    private static partial Regex UserFolderRx();

    /// <summary>The last <paramref name="lines"/> lines of a text file another process is writing.</summary>
    public static string Tail(string path, int lines, long maxBytes = 8 << 20)
    {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maxBytes) stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var all = reader.ReadToEnd().Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }

    /// <summary>
    /// The game log's Tartarus lines (those with "S16") from the last run's start: what the app reads of it, and
    /// nothing of the player's account, chat or other modes.
    /// </summary>
    public static string GameLogRun(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const long Window = 64 << 20;
        if (stream.Length > Window) stream.Seek(-Window, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = reader.ReadToEnd().Split('\n').Where(l => l.Contains("S16", StringComparison.Ordinal)).ToList();
        int start = lines.FindLastIndex(l => l.Contains("Btn_S16GamePlay_Start", StringComparison.Ordinal));
        var run = lines.Skip(Math.Max(0, start)).ToList();
        return string.Join('\n', run.Skip(Math.Max(0, run.Count - 30000)));
    }
}
