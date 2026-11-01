using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Tartarus.Vision.Ocr;

/// <summary>
/// Downloads an OCR language pack into <see cref="OcrPacks.DownloadDir"/>, each file checked by its SHA-256, from the
/// first of <see cref="Hosts"/> that gives it. A file cut off is kept (".part") and goes on from where it stopped.
/// </summary>
public static class OcrDownload
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// Where the packs are: Hugging Face, then its mirror in China, where Hugging Face is blocked (2026-11-01: a TapTap
    /// player's Chinese recognizer never came). The SHA-256 check makes any host as good as the first.
    /// </summary>
    public static readonly string[] Hosts = ["https://huggingface.co/", "https://hf-mirror.com/"];

    /// <summary>A host that sends nothing for this long is given up for the next.</summary>
    public static readonly TimeSpan Stall = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Times every host is tried for a file before it's given up (2026-11-01: a player's download failed when each of
    /// the two hosts stalled once, and started from nothing again at Retry).
    /// </summary>
    public const int Rounds = 3;

    /// <summary>
    /// Reports bytes done of the pack's whole size, and each host as it's tried. A file no host gives whole and right in
    /// <see cref="Rounds"/> tries each throws the last failure; the player's cancel throws at once.
    /// </summary>
    public static async Task RunAsync(OcrPack pack, IProgress<(long Done, long Total)> progress, CancellationToken cancel, IProgress<string>? host = null)
    {
        Directory.CreateDirectory(OcrPacks.DownloadDir);
        long before = 0;
        var files = pack.Files().ToList();
        host?.Report(string.Join(", ", Hosts.Select(h => new Uri(h).Host)));
        var hosts = await FastestFirstAsync(files[0].Path, cancel);
        foreach (var (name, filePath, size, sha256) in files)
        {
            for (int i = 0; ; i++)
            {
                int h = i % hosts.Count;
                host?.Report(new Uri(hosts[h]).Host);
                try
                {
                    await DownloadAsync(hosts[h] + filePath, name, size, sha256, done => progress.Report((before + done, pack.Size)), cancel);
                    // The host that gave this file is asked first for the next.
                    if (h > 0) (hosts[0], hosts[h]) = (hosts[h], hosts[0]);
                    break;
                }
                catch (Exception) when (!cancel.IsCancellationRequested && i + 1 < hosts.Count * Rounds)
                {
                }
            }
            before += size;
        }
    }

    /// <summary>
    /// <see cref="Hosts"/>, the first to answer for <paramref name="filePath"/> first: all asked at once, so a blocked one
    /// costs no wait (2026-11-01: a player in China cancelled twice within 5 s while huggingface.co said nothing, and
    /// the mirror would have been tried only after <see cref="Stall"/>). None answering keeps the order.
    /// </summary>
    private static async Task<List<string>> FastestFirstAsync(string filePath, CancellationToken cancel)
    {
        using var asking = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        asking.CancelAfter(Stall);
        var pending = Hosts.Select(async h =>
        {
            try
            {
                using var response = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, h + filePath),
                    HttpCompletionOption.ResponseHeadersRead, asking.Token);
                return response.IsSuccessStatusCode ? h : null;
            }
            catch (Exception) { return null; }
        }).ToList();
        string? first = null;
        while (pending.Count > 0 && first == null)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            first = done.Result;
        }
        asking.Cancel();
        cancel.ThrowIfCancellationRequested();
        var hosts = Hosts.ToList();
        if (first != null) { hosts.Remove(first); hosts.Insert(0, first); }
        return hosts;
    }

    private static async Task DownloadAsync(string url, string name, long size, string sha256, Action<long> progress, CancellationToken cancel)
    {
        string path = Path.Combine(OcrPacks.DownloadDir, name), part = path + ".part";
        // Given up when nothing comes for Stall: the connection, the headers, each piece.
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stalled.CancelAfter(Stall);
        try
        {
            // What an earlier try got goes on from where it stopped, if the host sends just the rest.
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have >= size) have = 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stalled.Token))
            {
                response.EnsureSuccessStatusCode();
                if (response.StatusCode != HttpStatusCode.PartialContent) have = 0;
                await using var source = await response.Content.ReadAsStreamAsync(stalled.Token);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                if (have > 0)
                {
                    await using var kept = File.OpenRead(part);
                    for (int n; (n = await kept.ReadAsync(buffer, cancel)) > 0;) hash.AppendData(buffer, 0, n);
                }
                await using var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write);
                long done = have;
                progress(done);
                for (int n; (n = await source.ReadAsync(buffer, stalled.Token)) > 0;)
                {
                    stalled.CancelAfter(Stall);
                    await file.WriteAsync(buffer.AsMemory(0, n), cancel);
                    hash.AppendData(buffer, 0, n);
                    done += n;
                    progress(done);
                }
                string got = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (got != sha256) throw new InvalidDataException($"{name}: SHA-256 {got}, expected {sha256}");
            }
            File.Move(part, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Cut off: kept for the next try. Wrong: not a byte of it trusted.
            if (ex is InvalidDataException) try { File.Delete(part); } catch (IOException) { }
            // A stalled host isn't the player's cancel.
            if (ex is OperationCanceledException && !cancel.IsCancellationRequested)
                throw new TimeoutException($"{new Uri(url).Host} sent nothing for {Stall.TotalSeconds:0} s", ex);
            throw;
        }
    }
}
