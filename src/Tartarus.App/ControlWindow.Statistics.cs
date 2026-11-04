using System.Globalization;
using System.Windows;
using Tartarus.App.Localization;

namespace Tartarus.App;

/// <summary>
/// The player's runs counted from the game's log: how many and their Chromatic Inmates on show; their time is kept too
/// (statistic.json, the app's log), not shown yet.
/// </summary>
public partial class ControlWindow
{
    private readonly RunStatistics _stats = RunStatistics.Load();

    /// <summary>Counts the runs of the log that ended since: the current one, or ones played while the app was closed.</summary>
    private void CountRuns()
    {
        if (_gameLog is not { } watcher) return;
        var counted = new List<Tartarus.Vision.LoggedRun>();
        lock (watcher.Log)
            foreach (var run in watcher.Log.Runs)
                if (_stats.Add(run)) counted.Add(run);
        foreach (var run in counted)
            Log($"Statistics: a run counted — Chromatic {run.ChromaticAtLeast}, {Duration(run.End!.Value - run.Start)}");
        if (counted.Count > 0) ShowStats();
    }

    private void ShowStats()
    {
        var s = _stats;
        StatsText.Text = s.Runs == 0
            ? Strings.Stats_NoRuns
            : Loc.F(Strings.Stats_Summary, s.Runs, s.Chromatic, s.ChromaticPerRun.ToString("0.00", CultureInfo.InvariantCulture));
        StatsText.ToolTip = Loc.F(Strings.Stats_Since,
            s.Since.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
    }

    /// <summary>"1:02:03", or "12:34" under an hour.</summary>
    private static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    private void ResetStatsButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Loc.F(Strings.Stats_ResetConfirm, _stats.Runs, _stats.Chromatic),
                Strings.Stats_DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _stats.Reset();
        Log("Statistics reset");
        ShowStats();
    }
}
