using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

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
        CleanUp();
        Directory.CreateDirectory(Staging);
        string zip = Path.Combine(Staging, release.Asset);
        using (var response = await Http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(zip);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                hash.AppendData(buffer, 0, n);
                done += n;
                progress?.Report(release.Size > 0 ? (double)done / release.Size : 0);
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the download doesn't match its SHA-256 on GitHub");
        }
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
            Process.Start(start);
        }
        catch (Exception ex)
        {
            Write($"Update to {AppVersion.Number} not installed: {ex.Message}");
            System.Windows.MessageBox.Show($"Couldn't put version {AppVersion.Number} into {target}:\n{ex.Message}\n\nIt is unpacked in {AppDir}: copy it over by hand or run it from there.",
                "Tartarus Solver: update", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppDir}\"") { UseShellExecute = true });
        }
    }

    /// <summary>What an update left behind (its zip, the unpacked version), unless it's running from there.</summary>
    public static void CleanUp()
    {
        if (AppDir.StartsWith(Staging, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(Staging)) return;
        try { Directory.Delete(Staging, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Write(string message)
    {
        try { File.AppendAllText(AppSettings.LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
        catch (IOException) { }
    }
}
