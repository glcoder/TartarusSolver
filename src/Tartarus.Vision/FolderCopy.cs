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
