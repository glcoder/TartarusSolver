using System.Text.Json;

namespace Tartarus.Vision;

/// <summary>
/// The player's runs since the statistics were last reset: how many, the Chromatic Inmates they ended with, the time
/// they took. A run counts once the game's log has seen it whole, from its Start to the Riot's result; kept in
/// statistic.json in the folder given (the app's: %LOCALAPPDATA%\TartarusSolver, beside the settings).
/// </summary>
public sealed class RunStatistics
{
    /// <summary>Counting from (the log's clock, UTC): runs started before it aren't counted.</summary>
    public DateTime Since { get; set; } = DateTime.UtcNow;
    public int Runs { get; set; }
    public int Chromatic { get; set; }
    public double Seconds { get; set; }
    /// <summary>
    /// The last runs counted, by their start and their end: every start of the app reads the game's log again. A run
    /// the game was restarted in has another start in the new log (its first day there) than the app saw live, but
    /// the same end (2026-10-30: one run counted twice).
    /// </summary>
    public List<DateTime> Counted { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public double ChromaticPerRun => Runs > 0 ? (double)Chromatic / Runs : 0;
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan Time => TimeSpan.FromSeconds(Seconds);
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan TimePerRun => Runs > 0 ? TimeSpan.FromSeconds(Seconds / Runs) : TimeSpan.Zero;

    private string _path = "";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static RunStatistics Load(string folder)
    {
        string path = Path.Combine(folder, "statistic.json");
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<RunStatistics>(File.ReadAllText(path), Options) is { } stats)
            {
                stats._path = path;
                return stats;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        // The first start: counting from now on (the runs already in the game's log were played before it).
        var fresh = new RunStatistics { _path = path };
        fresh.Save();
        return fresh;
    }

    /// <summary>Counts a run the log saw whole to its end, once; true when it was counted now.</summary>
    public bool Add(LoggedRun run)
    {
        if (run is not { Finished: true, FromStart: true, End: { } end } || run.Start < Since || Counted.Contains(run.Start) || Counted.Contains(end)) return false;
        Runs++;
        Chromatic += run.ChromaticAtLeast;
        Seconds += Math.Max(0, (end - run.Start).TotalSeconds);
        Counted.Add(run.Start);
        Counted.Add(end);
        if (Counted.Count > 200) Counted.RemoveRange(0, Counted.Count - 200);
        Save();
        return true;
    }

    public void Reset()
    {
        (Since, Runs, Chromatic, Seconds) = (DateTime.UtcNow, 0, 0, 0);
        Counted.Clear();
        Save();
    }

    private void Save()
    {
        try
        {
            // Whole or not at all: a half-written file was read as none and the statistics started over.
            AppFiles.WriteText(_path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
