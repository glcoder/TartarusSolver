using Tartarus.Rules;

namespace Tartarus.Solver;

/// <summary>
/// A state as the value network's input: the 16 cells counted clockwise from the Warden's (the ring looks the same
/// from any cell, so the network learns it once), then the run's globals. Floats in [0, 1].
/// </summary>
public static class Features
{
    public const int PerCell = 24;
    public const int Globals = 72;
    public const int Count = GameData.Cells * PerCell + Globals;

    public static void Encode(in GameState s, Span<float> x)
    {
        x[..Count].Clear();
        Span<byte> t = stackalloc byte[GameData.Cells];
        for (int k = 0; k < GameData.Cells; k++)
        {
            int n = (s.Position + k) % GameData.Cells;
            var f = x.Slice(k * PerCell, PerCell);
            ref readonly var c = ref s.Cells[n];
            if (n == GameData.Room)
            {
                f[0] = 1;
                continue;
            }
            if (c.Sealed)
            {
                f[1] = 1;
                if (c.Has(CellMarks.RarityShown) && !c.Inmate.IsNone) f[12 + Math.Min(4, (int)c.Inmate.Rarity)] = 1;
                if (c.Has(CellMarks.Scratch)) f[17] = 1;
                if (c.NestDue != 0)
                {
                    f[18] = 1;
                    f[19] = Math.Clamp((c.NestDue - s.Day) / 3f, 0, 1);
                }
            }
            else
            {
                f[2] = 1;
                f[3 + (int)c.Inmate.Code] = 1;
                f[7 + Math.Min(4, (int)c.Inmate.Rarity)] = 1;
                if (c.Has(CellMarks.Tusk)) f[20] = 1;
                for (int i = 0; i < s.PreyCount; i++)
                    if (s.Prey[i].Prey == c.Inmate.Id || s.Prey[i].Lion == c.Inmate.Id) f[21] = 1;
                if (Engine.Targets(s, n, t) > 0) f[23] = 1;
            }
            if (c.Marker != Incident.None) f[22] = 1;
        }

        var g = x.Slice(GameData.Cells * PerCell, Globals);
        int i0 = 0;
        g[i0++] = s.RemainingDays / 30f;
        g[i0++] = (float)s.Day / s.MaxDays;
        g[i0++] = s.MaxDays > GameData.Days ? 1 : 0;
        g[i0++] = s.Chromatic / 10f;
        g[i0++] = s.Chromatic >= GameData.ChromaticCap ? 1 : 0;
        for (int i = 0; i < GameData.InstrumentCount; i++) g[i0 + i] = s.Has((Instrument)i) ? 1 : 0;
        i0 += GameData.InstrumentCount;
        g[i0 + (int)s.Theme] = 1;
        i0 += 5;
        g[i0 + (int)s.Objective] = 1;
        i0 += 5;
        g[i0++] = (float)s.ObjectiveProgress / GameData.Target(s.Objective);
        g[i0++] = s.ObjectiveDone ? 1 : 0;
        g[i0++] = Math.Min(3, (int)s.Rerolls) / 3f;
        g[i0++] = s.ChainReaction ? 1 : 0;
        g[i0 + Math.Min(4, (int)s.UsurpationStage)] = 1;
        i0 += 5;
        // Today's Destinations by steps ahead (a morning's state).
        for (int i = 0; i < s.DestinationCount; i++)
        {
            int steps = Engine.Steps(s.Position, s.Destinations[i]);
            if (steps is >= 1 and <= 6) g[i0 + steps - 1] = 1;
        }
        i0 += 6;
        g[i0++] = s.Phase == Phase.Move ? 1 : 0;
        g[i0++] = s.Used != DayAction.None ? 1 : 0;
        g[i0++] = s.Replaced ? 1 : 0;
        // The Sandbox by rarity, and its Unopened cells.
        Span<int> by = stackalloc int[6];
        int sealedCount = 0;
        for (int n = 1; n < GameData.Cells; n++)
        {
            if (s.Cells[n].Sealed) sealedCount++;
            else by[(int)s.Cells[n].Inmate.Rarity]++;
        }
        for (int r = 0; r < 5; r++) g[i0 + r] = by[r] / 15f;
        i0 += 5;
        g[i0++] = sealedCount / 15f;
        System.Diagnostics.Debug.Assert(i0 <= Globals);
    }
}
