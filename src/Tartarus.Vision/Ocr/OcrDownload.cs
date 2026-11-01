using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Tartarus.Vision.Ocr;

/// <summary>
/// Downloads an OCR language pack into <see cref="OcrPacks.DownloadDir"/>, each file checked by its SHA-256, from the
/// first of <see cref="Hosts"/> that gives it.
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
    /// Reports bytes done of the pack's whole size, and each host as it's tried. A file no host gives whole and right
    /// throws the last host's failure; the player's cancel throws at once.
    /// </summary>
    public static async Task RunAsync(OcrPack pack, IProgress<(long Done, long Total)> progress, CancellationToken cancel, IProgress<string>? host = null)
    {
        Directory.CreateDirectory(OcrPacks.DownloadDir);
        long before = 0;
        var hosts = Hosts.ToList();
        foreach (var (name, filePath, size, sha256) in pack.Files())
        {
            for (int i = 0; ; i++)
            {
                host?.Report(new Uri(hosts[i]).Host);
                try
                {
                    await DownloadAsync(hosts[i] + filePath, name, sha256, done => progress.Report((before + done, pack.Size)), cancel);
                    // The host that gave this file is asked first for the next.
                    if (i > 0) (hosts[0], hosts[i]) = (hosts[i], hosts[0]);
                    break;
                }
                catch (Exception) when (!cancel.IsCancellationRequested && i + 1 < hosts.Count)
                {
                    progress.Report((before, pack.Size));
                }
            }
            before += size;
        }
    }

    private static async Task DownloadAsync(string url, string name, string sha256, Action<long> progress, CancellationToken cancel)
    {
        string path = Path.Combine(OcrPacks.DownloadDir, name), part = path + ".part";
        // Given up when nothing comes for Stall: the connection, the headers, each piece.
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stalled.CancelAfter(Stall);
        try
        {
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stalled.Token))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(stalled.Token);
                await using var file = File.Create(part);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long done = 0;
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
            try { File.Delete(part); } catch (IOException) { }
            // A stalled host isn't the player's cancel.
            if (ex is OperationCanceledException && !cancel.IsCancellationRequested)
                throw new TimeoutException($"{new Uri(url).Host} sent nothing for {Stall.TotalSeconds:0} s", ex);
            throw;
        }
    }
}
