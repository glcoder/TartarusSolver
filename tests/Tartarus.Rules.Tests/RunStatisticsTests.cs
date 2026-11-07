using Tartarus.Vision;

namespace Tartarus.Rules.Tests;

public class RunStatisticsTests
{
    [Fact]
    public void A_run_is_counted_once_also_when_a_game_restart_gave_it_another_start()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tartarus-stats-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stats = RunStatistics.Load(dir);
            var start = DateTime.UtcNow.AddMinutes(1);
            var end = start.AddMinutes(5);
            Assert.True(stats.Add(new LoggedRun { Start = start, End = end, Finished = true, FromStart = true, Chromatic = 7 }));
            // The same run read again from the log, and read from the new log after the game was restarted mid-run.
            Assert.False(stats.Add(new LoggedRun { Start = start, End = end, Finished = true, FromStart = true, Chromatic = 7 }));
            Assert.False(stats.Add(new LoggedRun { Start = start.AddMinutes(2), End = end, Finished = true, FromStart = true, Chromatic = 7 }));
            // Kept whole on disk: the next start reads the same count.
            var again = RunStatistics.Load(dir);
            Assert.Equal((1, 7), (again.Runs, again.Chromatic));
            Assert.False(again.Add(new LoggedRun { Start = start, End = end, Finished = true, FromStart = true, Chromatic = 7 }));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
