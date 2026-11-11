namespace Tartarus.Vision;

/// <summary>
/// One folder's files put over another's, all or none as far as copying goes: every file is first copied beside its
/// place (name + ".new"), and only when all of them are there do they take their places, by renames. A copy that fails
/// (an antivirus holding a file, a full disk) leaves the target as it was. An update copied file by file straight over
/// the install could stop half way, old and new files mixed (2026-11-11 review). Files only the target has stay.
/// </summary>
public static class FolderCopy
{
    public const string Staged = ".new";

    /// <param name="tries">Tries per file: an antivirus may hold a fresh file a moment.</param>
    public static void Over(string from, string to, int tries = 20, int waitMs = 500)
    {
        var staged = new List<(string New, string Place)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string place = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(place)!);
                Retry(() => File.Copy(file, place + Staged, overwrite: true), tries, waitMs);
                staged.Add((place + Staged, place));
            }
        }
        catch
        {
            foreach (var (copy, _) in staged)
                try { File.Delete(copy); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        foreach (var (copy, place) in staged) Retry(() => File.Move(copy, place, overwrite: true), tries, waitMs);
    }

    /// <summary>The Visual C++ runtime files a "-full" release carries (tools/package.py): Windows's own, the same in every version.</summary>
    public static bool IsVcRuntime(string path) =>
        Path.GetFileName(path).ToLowerInvariant() is "msvcp140.dll" or "msvcp140_1.dll" or "vcruntime140.dll" or "vcruntime140_1.dll";

    /// <summary>
    /// Unpacks a zip into <paramref name="to"/>. A file <paramref name="mayMiss"/> says can be done without, that Windows
    /// won't let be written, is left out and told to <paramref name="missed"/> (2026-11-11: on two players' PCs writing
    /// msvcp140.dll under AppData was refused and no update ever unpacked, though the install had that file already and
    /// keeps it). Nothing is written outside <paramref name="to"/>.
    /// </summary>
    public static void Unzip(string zip, string to, Func<string, bool> mayMiss, Action<string, Exception> missed)
    {
        string root = Path.GetFullPath(to) + Path.DirectorySeparatorChar;
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            string path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException($"{entry.FullName} would be unpacked outside {to}");
            // A folder's own entry.
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                using var from = entry.Open();
                using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                from.CopyTo(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && mayMiss(path))
            {
                missed(entry.FullName, ex);
            }
        }
    }

    private static void Retry(Action act, int tries, int waitMs)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                act();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < tries)
            {
                Thread.Sleep(waitMs);
            }
        }
    }
}
