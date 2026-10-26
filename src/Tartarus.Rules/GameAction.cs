namespace Tartarus.Rules;

public enum ActionKind : byte
{
    /// <summary>Take an instrument of the offer (<see cref="GameAction.Arg"/>: the <see cref="Rules.Instrument"/>).</summary>
    PickInstrument,
    /// <summary>Re-roll the instrument offer.</summary>
    Reroll,
    /// <summary>Take a Warden's Treasure (Arg: the <see cref="Rules.Treasure"/>).</summary>
    PickTreasure,
    /// <summary>Interrogation Room: Instigate (Arg 0) or Bestow (Arg 1).</summary>
    Interrogate,
    /// <summary>Chain Consume: the eater consumes the Inmate in cell Arg.</summary>
    Chain,
    /// <summary>Stop the chain (the game's Cancel).</summary>
    StopChain,
    /// <summary>Sewer Ratway: move to cell Arg.</summary>
    Ratway,
    /// <summary>Select a Destination: move to cell Arg.</summary>
    Move,
    /// <summary>The Inmate in the Warden's cell consumes the one in cell Arg (through Scratch Marks if Unopened).</summary>
    Consume,
    /// <summary>Replace the Inmate in the Warden's cell.</summary>
    Replace,
    /// <summary>Hunt: the Lion in the Warden's cell puts a Prey mark on the Inmate in cell Arg.</summary>
    Hunt,
    /// <summary>End the day.</summary>
    End,
}

/// <summary>One choice of the player. Cells are 0 (the Interrogation Room) .. 15, screen cell number minus one.</summary>
public readonly record struct GameAction(ActionKind Kind, byte Arg = 0)
{
    public static GameAction Instigate => new(ActionKind.Interrogate, 0);
    public static GameAction Bestow => new(ActionKind.Interrogate, 1);

    public override string ToString() => Kind switch
    {
        ActionKind.PickInstrument => $"take {GameData.Info((Instrument)Arg).Name}",
        ActionKind.Reroll => "Re-roll",
        ActionKind.PickTreasure => $"take {GameData.Name((Treasure)Arg)}",
        ActionKind.Interrogate => Arg == 0 ? "Instigate" : "Bestow",
        ActionKind.Chain => $"Chain Consume cell {Arg + 1}",
        ActionKind.StopChain => "stop the chain",
        ActionKind.Ratway => $"Sewer Ratway to cell {Arg + 1}",
        ActionKind.Move => Arg == GameData.Room ? "go to the Interrogation Room" : $"go to cell {Arg + 1}",
        ActionKind.Consume => $"Consume cell {Arg + 1}",
        ActionKind.Replace => "Replace",
        ActionKind.Hunt => $"Hunt cell {Arg + 1}",
        _ => "End",
    };
}
