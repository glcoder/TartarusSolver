using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tartarus.Vision;

namespace Tartarus.App;

/// <summary>
/// New versions from the project's GitHub releases: the latest one, its zip (the same kind as this install: with .NET or
/// without) downloaded and checked against the SHA-256 GitHub gives, and put in place of this install by the new
/// version itself (<see cref="InstallHere"/>) once this one has exited. Settings and downloaded OCR languages live in
/// %LOCALAPPDATA% and stay as they are.
/// </summary>
public static class Updates
{
    private const string Repo = "glcoder/TartarusSolver";

    public sealed record Release(Version Version, string Page, string Notes, string? Asset, string? Url, long Size, string? Sha256);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API wants a User-Agent; nothing else about the player goes with the request.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TartarusSolver/" + AppVersion.Number);
        return http;
    }

    /// <summary>
    /// A connection that sends nothing for this long is given up. Slower than the OCR download's: the zip comes from one
    /// host, there's no other to go to (HttpClient's own Timeout ends at the headers, a stalled body would wait for ever).
    /// </summary>
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Tries in a row that bring no bytes before the player is told (2026-11-04: 13 of 152 installs in reports had a
    /// download fail: SSL, connect timeouts, "response ended prematurely"; 10 of them stayed on their version).
    /// </summary>
    private const int IdleTries = 5;
    private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(2);

    private static string AppDir => AppContext.BaseDirectory.TrimEnd('\\', '/');
    private static string Staging => Path.Combine(AppSettings.Folder, "update");

    /// <summary>Unpacked from a release zip (its README.txt beside the exe): only such a folder is updated in place, never a build.</summary>
    public static bool Installed => File.Exists(Path.Combine(AppDir, "README.txt"));

    /// <summary>The "-full" kind, .NET included.</summary>
    private static bool SelfContained => File.Exists(Path.Combine(AppDir, "coreclr.dll"));

    public static Version Current { get; } = Version.TryParse(AppVersion.Number, out var v) ? v : new Version(0, 0, 0);

    /// <summary>The latest release, with this install's kind of zip when it has one with a digest.</summary>
    public static async Task<Release?> LatestAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct));
        var root = doc.RootElement;
        if (root.GetProperty("tag_name").GetString() is not { } tag || !Version.TryParse(tag.TrimStart('v'), out var version)) return null;
        string page = root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases/latest";
        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        string name = $"TartarusSolver-{version}{(SelfContained ? "-full" : "")}.zip";
        foreach (var a in root.GetProperty("assets").EnumerateArray())
            if (a.GetProperty("name").GetString() == name && a.TryGetProperty("digest", out var d) && d.GetString() is { } digest
                && digest.StartsWith("sha256:", StringComparison.Ordinal))
                return new Release(version, page, notes, name, a.GetProperty("browser_download_url").GetString(), a.GetProperty("size").GetInt64(), digest[7..]);
        return new Release(version, page, notes, null, null, 0, null);
    }

    /// <summary>Downloads the release's zip, checks it, unpacks it; returns the folder of the new version's exe.</summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (release.Url == null || release.Sha256 == null || release.Asset == null) throw new InvalidOperationException("the release has no zip for this install");
        await _cleaning;
        await Task.Run(CleanUp, ct);
        Directory.CreateDirectory(Staging);
        string zip = Path.Combine(Staging, release.Asset), part = ResumingDownload.PartOf(zip);
        foreach (var other in Directory.EnumerateFiles(Staging, "*.part"))
            if (!other.Equals(part, StringComparison.OrdinalIgnoreCase)) Delete(other);
        if (File.Exists(part)) AppLog.Write($"Update: {release.Asset}: {new FileInfo(part).Length} bytes kept from an earlier try");
        int shown = -1;
        await Task.Run(() => ResumingDownload.UntilWholeAsync(Http, release.Url, zip, release.Size, release.Sha256, Stall, IdleTries, FirstWait,
            done =>
            {
                int percent = release.Size > 0 ? (int)(done * 100 / release.Size) : 0;
                if (percent == shown) return;
                shown = percent;
                progress?.Report(percent / 100.0);
            },
            ex => AppLog.Write($"Update: download cut off at {shown}%, trying again: {ex.GetBaseException().Message}"), ct), ct);
        string unpacked = Path.Combine(Staging, release.Version.ToString());
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, unpacked), ct);
        File.Delete(zip);
        // The zip holds one folder, TartarusSolver.
        string app = Path.Combine(unpacked, "TartarusSolver");
        if (!File.Exists(Path.Combine(app, "TartarusSolver.exe"))) throw new InvalidDataException("no TartarusSolver.exe in the zip");
        return app;
    }

    /// <summary>Starts the new version (in <paramref name="app"/>) to put itself in this install's place once this one exits.</summary>
    public static void StartInstall(string app, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(Path.Combine(app, "TartarusSolver.exe")) { UseShellExecute = false, WorkingDirectory = app };
        start.ArgumentList.Add("--install-update");
        start.ArgumentList.Add(AppDir);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        foreach (var a in args) start.ArgumentList.Add(a);
        Process.Start(start);
    }

    /// <summary>
    /// "--install-update target pid args...", run by the new version from where it was unpacked: waits for the old one
    /// to exit, copies itself over the target folder (files the new version doesn't have stay), starts it there with the
    /// old one's arguments. On a failure the player is told and shown the unpacked folder.
    /// </summary>
    public static void InstallHere(string[] args)
    {
        string target = args[0];
        try
        {
            try
            {
                using var old = Process.GetProcessById(int.Parse(args[1]));
                old.WaitForExit(30_000);
            }
            catch (ArgumentException) { } // gone already
            // The installer is the one app while it copies: a start meanwhile would run half-copied files. Free again as the
            // old one exits (or it would be gone already).
            SingleInstance.Take(TimeSpan.FromSeconds(30));
            foreach (var file in Directory.EnumerateFiles(AppDir, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(target, Path.GetRelativePath(AppDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                // An antivirus may hold a fresh file a moment.
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.Copy(file, to, overwrite: true);
                        break;
                    }
                    catch (IOException) when (attempt < 20)
                    {
                        Thread.Sleep(500);
                    }
                }
            }
            Write($"Updated to {AppVersion.Number} in {target}");
            var start = new ProcessStartInfo(Path.Combine(target, "TartarusSolver.exe")) { UseShellExecute = false, WorkingDirectory = target };
            foreach (var a in args.Skip(2)) start.ArgumentList.Add(a);
            SingleInstance.Release();
            Process.Start(start);
        }
        catch (Exception ex)
        {
            SingleInstance.Release();
            Write($"Update to {AppVersion.Number} not installed: {ex.Message}");
            System.Windows.MessageBox.Show($"Couldn't put version {AppVersion.Number} into {target}:\n{ex.Message}\n\nIt is unpacked in {AppDir}: copy it over by hand or run it from there.",
                "Tartarus Solver: update", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppDir}\"") { UseShellExecute = true });
        }
    }

    /// <summary>
    /// <see cref="CleanUp"/> off the UI thread, for the start: hundreds of files, an antivirus looks at each (2026-11-03: the
    /// window stood still for 30 s on the first start after an update).
    /// </summary>
    public static void CleanUpBehind() => _cleaning = Task.Run(CleanUp);
    private static Task _cleaning = Task.CompletedTask;

    /// <summary>
    /// What an update left behind (its zip, the unpacked version), unless it's running from there. A download cut off
    /// stays while it's of a version newer than this one: the next try goes on from there.
    /// </summary>
    public static void CleanUp()
    {
        if (AppDir.StartsWith(Staging, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(Staging)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Staging)) Directory.Delete(dir, recursive: true);
            foreach (var file in Directory.EnumerateFiles(Staging))
                if (!StillWanted(file)) Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool StillWanted(string file) =>
        Regex.Match(Path.GetFileName(file), @"^TartarusSolver-(\d+\.\d+\.\d+)(-full)?\.zip\.part$") is { Success: true } m
        && Version.Parse(m.Groups[1].Value) > Current;

    private static void Delete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Write(string message)
    {
        try { File.AppendAllText(AppSettings.LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch (IOException) { }
    }
}
