using Tartarus.Rules;

namespace Tartarus.Rules.Tests;

/// <summary>Hand-made positions. Cells by index (0 the Interrogation Room .. 15), as the engine counts them.</summary>
internal static class Setup
{
    /// <summary>An engine that never ends the day by itself, so a test sees the state right after its action.</summary>
    public static readonly Engine Manual = new() { AutoEnd = false };

    /// <summary>Day 5 of 20, Make Your Decision, the Warden on cell <paramref name="warden"/>, every cell Unopened.</summary>
    public static GameState Empty(int warden = 3, Theme theme = Theme.None, ulong seed = 1)
    {
        var s = new GameState
        {
            Rng = new Rng(seed),
            NextId = 1,
            Day = 5,
            MaxDays = (byte)(theme == Theme.IronLaw ? 30 : 20),
            Phase = Phase.Act,
            Position = (byte)warden,
            Theme = theme,
            Objective = ObjectiveKind.PrisonWalk,
        };
        for (int n = 1; n < GameData.Cells; n++) s.Cells[n].Sealed = true;
        return s;
    }

    public static ushort Put(ref GameState s, int cell, Code code, Rarity rarity)
    {
        ushort id = s.NextId++;
        s.Cells[cell].Sealed = false;
        s.Cells[cell].Inmate = new Inmate { Id = id, Code = code, Rarity = rarity };
        return id;
    }

    /// <summary>An Unopened cell with a known Inmate behind it (its rarity shown).</summary>
    public static ushort Behind(ref GameState s, int cell, Code code, Rarity rarity, CellMarks marks = CellMarks.RarityShown)
    {
        ushort id = s.NextId++;
        s.Cells[cell].Sealed = true;
        s.Cells[cell].Inmate = new Inmate { Id = id, Code = code, Rarity = rarity };
        s.Cells[cell].Marks = marks;
        return id;
    }

    public static void Hold(ref GameState s, params Instrument[] instruments)
    {
        foreach (var i in instruments) s.Instruments |= 1u << (int)i;
    }

    public static List<GameAction> Legal(Engine e, in GameState s)
    {
        Span<GameAction> buf = stackalloc GameAction[Engine.MaxActions];
        int k = e.Legal(s, buf);
        return buf[..k].ToArray().ToList();
    }

    public static void Do(Engine e, ref GameState s, GameAction a) => Assert.True(e.Apply(ref s, a), $"{a} should be legal: {StateText.Describe(s)}");

    public static Inmate At(in GameState s, int cell) => s.Cells[cell].Inmate;
}
