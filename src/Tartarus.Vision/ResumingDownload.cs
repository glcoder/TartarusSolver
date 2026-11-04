using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Tartarus.Vision;

/// <summary>
/// A big file over a bad connection, checked by its SHA-256. What a try got is kept beside it (".part") and the next
/// goes on from there; only a file whose SHA-256 is right takes its name.
/// </summary>
public static class ResumingDownload
{
    public static string PartOf(string path) => path + ".part";

    /// <summary>One try. Given up when nothing comes for <paramref name="stall"/>: the connection, the headers, each piece.</summary>
    public static async Task GetAsync(HttpClient http, string url, string path, long size, string sha256, TimeSpan stall, Action<long> progress, CancellationToken cancel)
    {
        string part = PartOf(path);
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stalled.CancelAfter(stall);
        try
        {
            // What an earlier try got goes on from where it stopped, if the host sends just the rest.
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have >= size) have = 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stalled.Token))
            {
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) throw new InvalidDataException($"{Path.GetFileName(path)}: the host has no such part");
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
                    stalled.CancelAfter(stall);
                    await file.WriteAsync(buffer.AsMemory(0, n), cancel);
                    hash.AppendData(buffer, 0, n);
                    done += n;
                    progress(done);
                }
                string got = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!got.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{Path.GetFileName(path)}: SHA-256 {got}, expected {sha256}");
            }
            File.Move(part, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Cut off: kept for the next try. Wrong: not a byte of it trusted.
            if (ex is InvalidDataException) try { File.Delete(part); } catch (IOException) { }
            // A stalled host isn't the player's cancel.
            if (ex is OperationCanceledException && !cancel.IsCancellationRequested)
                throw new TimeoutException($"{new Uri(url).Host} sent nothing for {stall.TotalSeconds:0} s", ex);
            throw;
        }
    }

    /// <summary>
    /// Tries again for as long as tries bring bytes; <paramref name="idleTries"/> in a row that bring none throw the last
    /// failure, waiting <paramref name="firstWait"/> after the first of them and twice as long after each next. A file
    /// that comes whole from its first byte and wrong throws at once: another 100 MB wouldn't be any better.
    /// </summary>
    public static async Task UntilWholeAsync(HttpClient http, string url, string path, long size, string sha256, TimeSpan stall, int idleTries, TimeSpan firstWait,
        Action<long> progress, Action<Exception>? retrying, CancellationToken cancel)
    {
        string part = PartOf(path);
        long Have() => File.Exists(part) ? new FileInfo(part).Length : 0;
        for (int idle = 0; ;)
        {
            long before = Have();
            try
            {
                await GetAsync(http, url, path, size, sha256, stall, progress, cancel);
                return;
            }
            catch (Exception ex) when (!cancel.IsCancellationRequested)
            {
                if (ex is InvalidDataException && before == 0) throw;
                idle = Have() > before ? 0 : idle + 1;
                if (idle >= idleTries) throw;
                retrying?.Invoke(ex);
                await Task.Delay(firstWait * (1 << Math.Max(0, idle - 1)), cancel);
            }
        }
    }
}
