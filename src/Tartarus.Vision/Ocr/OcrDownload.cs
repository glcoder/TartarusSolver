using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Tartarus.Vision.Ocr;

/// <summary>Downloads an OCR language pack into <see cref="OcrPacks.DownloadDir"/>, each file checked by its SHA-256.</summary>
public static class OcrDownload
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Reports bytes done of the pack's whole size. A file that fails its check is deleted and throws.</summary>
    public static async Task RunAsync(OcrPack pack, IProgress<(long Done, long Total)> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(OcrPacks.DownloadDir);
        long before = 0;
        foreach (var (name, url, size, sha256) in pack.Files())
        {
            string path = Path.Combine(OcrPacks.DownloadDir, name), part = path + ".part";
            try
            {
                using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel))
                {
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(cancel);
                    await using var file = File.Create(part);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    for (int n; (n = await source.ReadAsync(buffer, cancel)) > 0;)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, n), cancel);
                        hash.AppendData(buffer, 0, n);
                        done += n;
                        progress.Report((before + done, pack.Size));
                    }
                    string got = Convert.ToHexStringLower(hash.GetHashAndReset());
                    if (got != sha256) throw new InvalidDataException($"{name}: SHA-256 {got}, expected {sha256}");
                }
                File.Move(part, path, overwrite: true);
                before += size;
            }
            catch
            {
                try { File.Delete(part); } catch (IOException) { }
                throw;
            }
        }
    }
}
