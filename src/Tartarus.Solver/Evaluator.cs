using Tartarus.Rules;

namespace Tartarus.Solver;

/// <summary>
/// The weights of the static evaluation, in Chromatic units (one Chromatic contained = 1). Tuned by playing many
/// runs on the same seeds (tartarus-sim tune).
/// </summary>
public sealed record EvalWeights
{
    /// <summary>What an Inmate on the Sandbox is worth by rarity (Normal .. Ultimate) early in a run: its chance to become Chromatic, roughly.</summary>
    public double[] Potential { get; init; } = [0.02, 0.05, 0.12, 0.28, 0.55];
    /// <summary>Days before the end over which an Inmate's potential fades into what it counts at the Riot.</summary>
    public double Horizon { get; init; } = 6;
    /// <summary>An Inmate with a Consumable target, times (rarity + 1) / 5: a merge waiting.</summary>
    public double MergeReady { get; init; } = 0.06;
    /// <summary>An Unopened cell: a door that will bring an Inmate.</summary>
    public double Unopened { get; init; } = 0.015;
    /// <summary>A Prison Incident marker.</summary>
    public double Marker { get; init; } = 0.04;
    /// <summary>The Objective's second instrument pick, times the share done.</summary>
    public double Objective { get; init; } = 0.3;
    /// <summary>
    /// What holding each instrument is worth over a whole run (data order), measured by playing runs that take it
    /// at the start (tartarus-sim instruments); it fades as the days run out.
    /// </summary>
    public double[] InstrumentValue { get; init; } = Enumerable.Repeat(0.3, GameData.InstrumentCount).ToArray();
    /// <summary>Re-roll an offer whose best card is worth less than this.</summary>
    public double RerollBelow { get; init; } = 0.25;

    public static readonly EvalWeights Default = new();
}

/// <summary>
/// The static evaluation: what a position is worth in Chromatic units, without playing it out. The final result
/// at the Riot (<see cref="RunScore.Utility"/>) when the run is over; before that, what's contained plus what the
/// Sandbox promises, fading into the final count as the days run out.
/// </summary>
public static class Evaluator
{
    public static double Value(in GameState s, EvalWeights w)
    {
        // What each rarity counts at the Riot, as RunScore.Utility weighs it.
        var final = RunScore.Worth;
        if (s.Ended) return RunScore.Of(s).Utility;
        double days = s.MaxDays - s.Day + (s.Phase == Phase.Move ? 1 : 0.5);
        double f = Math.Clamp(days / w.Horizon, 0, 1);
        bool capped = s.Chromatic >= GameData.ChromaticCap;
        double v = s.Chromatic;
        int sealedCount = 0, markers = 0;
        Span<byte> t = stackalloc byte[GameData.Cells];
        for (int n = 1; n < GameData.Cells; n++)
        {
            ref readonly var c = ref s.Cells[n];
            if (c.Marker != Incident.None) markers++;
            if (c.Sealed)
            {
                sealedCount++;
                continue;
            }
            int r = (int)c.Inmate.Rarity;
            double potential = capped ? w.Potential[r] / w.Potential[4] * final[4] : w.Potential[r];
            v += f * potential + (1 - f) * final[r];
            if (Engine.Targets(s, n, t) > 0) v += f * w.MergeReady * (r + 1) / 5.0 * (capped ? final[4] / w.Potential[4] : 1);
        }
        v += f * (w.Unopened * sealedCount + w.Marker * markers);
        if (!s.ObjectiveDone) v += w.Objective * f * s.ObjectiveProgress / GameData.Target(s.Objective);
        double inst = 0;
        for (int i = 0; i < GameData.InstrumentCount; i++)
            if (s.Has((Instrument)i)) inst += w.InstrumentValue[i];
        // A pick waiting: the best card's worth (it will be taken).
        for (int i = 0; i < s.PendingCount; i++)
        {
            var p = s.Pending[i];
            if (p.Kind != PendingKind.Instrument) continue;
            double best = 0;
            for (int k = 0; k < p.Count; k++) best = Math.Max(best, w.InstrumentValue[(int)p.Offer(k)]);
            inst += best;
        }
        v += inst * Math.Clamp(days / s.MaxDays * 1.5, 0, 1);
        return v;
    }
}
