using Tartarus.Solver;

namespace Tartarus.Rules.Tests;

public class ChestCalibrationTests
{
    [Fact]
    public void The_fit_turns_expectations_into_what_the_runs_ended_with()
    {
        // Runs end half a Chromatic above the expectation with 10 days left, as they did with the search's hints.
        var rows = new List<(int, double, double, int, int)>();
        for (int i = 0; i < 400; i++)
        {
            double expected = 4 + i % 5;
            rows.Add((10, expected, 2, (int)expected + (i % 2), 3));
        }
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, ChestCalibration.Fit(rows, "test"));
            var c = ChestCalibration.Load(path)!;
            var (chromatic, ultimate) = c.Adjust(10, 6, 2);
            Assert.Equal(6.5, chromatic, 2);
            Assert.Equal(3, ultimate, 2);
            // Never past the cap of 10, and a day the table doesn't have stays as searched.
            Assert.Equal(10, c.Adjust(10, 9.9, 2).Chromatic, 6);
            Assert.Equal((6, 2), c.Adjust(3, 6, 2));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
