using System.Globalization;
using System.Text;
using Tartarus.Rules;

namespace Tartarus.Solver;

/// <summary>
/// <see cref="EvalWeights"/> as text, one "name = value[, value ...]" per line (instrument values by id), so tuned
/// weights can be kept, read and diffed. No reflection: AOT-safe.
/// </summary>
public static class WeightsFile
{
    public static string Write(EvalWeights w)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Tartarus move hints: static evaluation weights (tartarus-sim tune / instruments)");
        sb.AppendLine($"potential = {string.Join(", ", w.Potential.Select(F))}");
        sb.AppendLine($"horizon = {F(w.Horizon)}");
        sb.AppendLine($"merge_ready = {F(w.MergeReady)}");
        sb.AppendLine($"unopened = {F(w.Unopened)}");
        sb.AppendLine($"marker = {F(w.Marker)}");
        sb.AppendLine($"objective = {F(w.Objective)}");
        sb.AppendLine($"reroll_below = {F(w.RerollBelow)}");
        for (int i = 0; i < GameData.InstrumentCount; i++)
            sb.AppendLine($"instrument.{GameData.Info((Instrument)i).Id} = {F(w.InstrumentValue[i])}");
        return sb.ToString();

        static string F(double x) => x.ToString("0.#####", CultureInfo.InvariantCulture);
    }

    /// <summary>models/solver/weights.txt beside the program or in the repository it runs from; null if there is none.</summary>
    public static string? Find() => AppFiles.Find("models", "solver", "weights.txt");

    public static EvalWeights Read(string text)
    {
        var w = new EvalWeights();
        var potential = (double[])w.Potential.Clone();
        var inst = (double[])w.InstrumentValue.Clone();
        double horizon = w.Horizon, merge = w.MergeReady, unopened = w.Unopened, marker = w.Marker, objective = w.Objective, reroll = w.RerollBelow;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line[..eq].Trim();
            var values = line[(eq + 1)..].Split(',').Select(v => double.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
            switch (key)
            {
                case "potential": for (int i = 0; i < Math.Min(5, values.Length); i++) potential[i] = values[i]; break;
                case "horizon": horizon = values[0]; break;
                case "merge_ready": merge = values[0]; break;
                case "unopened": unopened = values[0]; break;
                case "marker": marker = values[0]; break;
                case "objective": objective = values[0]; break;
                case "reroll_below": reroll = values[0]; break;
                default:
                    if (key.StartsWith("instrument.", StringComparison.Ordinal) && GameData.InstrumentById(key["instrument.".Length..]) is { } id)
                        inst[(int)id] = values[0];
                    break;
            }
        }
        return w with
        {
            Potential = potential, Horizon = horizon, MergeReady = merge, Unopened = unopened, Marker = marker,
            Objective = objective, RerollBelow = reroll, InstrumentValue = inst,
        };
    }
}
