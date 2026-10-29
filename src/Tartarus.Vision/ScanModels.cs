using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tartarus.Vision;

// What a scanner read from a frame, in the game's terms (see SandboxLabels.cs). js/scan.js answers in its
// own words (beast, tier, locked, doors, tools ...); ScanJson.Parse brings its JSON, and scans saved before
// the switch, to these names.

public sealed class ScanResult
{
    public bool Ok { get; set; }
    public string? Reason { get; set; }
    public SandboxLoc? Loc { get; set; }
    /// <summary>16 cells; index 0 is the Interrogation Room (cell 01), index n is cell n+1.</summary>
    public List<CellRead>? Cells { get; set; }
    public WardenRead? Warden { get; set; }
    public DestinationsRead? Destinations { get; set; }
    /// <summary>Remaining Days.</summary>
    public DaysRead? Days { get; set; }
    public InstrumentsRead? Instruments { get; set; }
    public ChromaticRead? Chromatic { get; set; }
    /// <summary>A Torture Instrument or Warden's Treasure pick, when one is on screen instead of the Sandbox.</summary>
    public PickRead? Pick { get; set; }
    /// <summary>The run theme screen before a run (Tyranny, Opulence, Iron Law, Cage Fight): the theme's id.</summary>
    public string? Theme { get; set; }
    public List<string>? Notes { get; set; }
    public double Ms { get; set; }

    /// <summary>Scanner that produced this result ("layout+cnn", "scan.js", ...).</summary>
    [JsonIgnore] public string Source { get; set; } = "";
    /// <summary>This result as JSON, kept with dataset samples.</summary>
    [JsonIgnore] public string RawJson { get; set; } = "";
    /// <summary>Wall-clock time of the whole call, including marshalling.</summary>
    [JsonIgnore] public double TotalMs { get; set; }
    /// <summary>The screen's text, when the scanner ran OCR (days, destinations, objective, title).</summary>
    [JsonIgnore] public Ocr.ScreenText? Text { get; set; }
    /// <summary>Where the time went, by stage ("ocr", "cells", ...), in milliseconds.</summary>
    [JsonIgnore] public Dictionary<string, double> Timings { get; } = [];
    /// <summary>The screen layout the scan used, when it came from <see cref="ScreenLayout"/>.</summary>
    [JsonIgnore] public ScreenLayout? Layout { get; set; }
}

/// <summary>
/// Where the Sandbox is in the frame: an image point is S * (reference point) + (Tx, Ty), the reference being
/// js/scan.js's homography H0. Score: how sure the scanner is that the Sandbox is on screen.
/// </summary>
public sealed class SandboxLoc
{
    public double S { get; set; }
    public double Tx { get; set; }
    public double Ty { get; set; }
    public double Score { get; set; }
}

public sealed class CellRead
{
    public const string InterrogationRoom = "interrogationRoom", Unopened = "unopened", Inmate = "inmate", Empty = "empty";
    /// <summary>The game's UI (a tooltip) hides the cell: nothing to read there now.</summary>
    public const string Covered = "covered";

    public int N { get; set; }
    /// <summary><see cref="InterrogationRoom"/>, <see cref="Unopened"/>, <see cref="Inmate"/>, <see cref="Empty"/> or <see cref="Covered"/>.</summary>
    public string Kind { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Pixels per reference pixel at this cell (lower rows are drawn larger).</summary>
    public double K { get; set; }
    public double Conf { get; set; }
    public bool Nest { get; set; }
    /// <summary>Null when the portrait can't be told.</summary>
    public Code? Code { get; set; }
    public Rarity? Rarity { get; set; }
    /// <summary>A Consumable target (the red jaws over the portrait); null when unknown.</summary>
    public bool? Consumable { get; set; }
    /// <summary>A Prison Incident ("!") on this cell.</summary>
    public bool Incident { get; set; }
    /// <summary>An Inmate with a Tusk mark (Iron Tongs of Lust: Mammoth).</summary>
    public bool Tusk { get; set; }
    /// <summary>An Inmate with a Prey mark (Blood-Stained Trap's Hunt).</summary>
    public bool Prey { get; set; }
    /// <summary>An Unopened cell whose Inmate's rarity shows (Scratch Marks, Crow Whispers, the Scales).</summary>
    public VisibleRarityRead? VisibleRarity { get; set; }

    [JsonIgnore] public bool IsInmate => Kind == Inmate;
    [JsonIgnore] public bool IsUnopened => Kind == Unopened;
}

public sealed class WardenRead
{
    /// <summary>0-based cell index (Interrogation Room = 0).</summary>
    public int Cell { get; set; }
    public double Conf { get; set; }
}

/// <summary>The rarity an Unopened cell shows (a "?" portrait in the rarity's frame), and whether it has Scratch Marks.</summary>
public sealed class VisibleRarityRead
{
    public Rarity Rarity { get; set; }
    public bool ScratchMarks { get; set; }
    public double Conf { get; set; }
}

public sealed class DestinationsRead
{
    /// <summary>0-based cell indices, in card order left to right.</summary>
    public List<int> Cells { get; set; } = [];
    public double Conf { get; set; }
}

public sealed class DaysRead
{
    public int Days { get; set; }
    public double Conf { get; set; }
}

public sealed class InstrumentsRead
{
    public bool Found { get; set; }
    public List<InstrumentRead> Instruments { get; set; } = [];
}

/// <summary>A Torture Instrument held.</summary>
public sealed class InstrumentRead
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public double Score { get; set; }
    public double Conf { get; set; }
}

/// <summary>
/// A pick screen in place of the Sandbox: "Select a Torture Instrument" (three instruments, at the run's start and
/// for the Objective) or the Warden's Treasure a Chromatic brings (four rewards). The cards left to right.
/// </summary>
public sealed class PickRead
{
    public const string Instrument = "instrument", Treasure = "treasure";

    /// <summary><see cref="Instrument"/> or <see cref="Treasure"/>.</summary>
    public string Kind { get; set; } = Instrument;
    public List<PickCardRead> Cards { get; set; } = [];
    /// <summary>"Remaining attempts N" over the Re-roll button; null when there is no Re-roll.</summary>
    public int? Rerolls { get; set; }
}

/// <summary>A pick card: index from the left, and what its name is (an instrument or treasure id; null if none matched).</summary>
public sealed class PickCardRead
{
    public int Index { get; set; }
    public string? Id { get; set; }
    /// <summary>The name as OCR read it.</summary>
    public string Text { get; set; } = "";
    /// <summary>How alike the read name and the matched name are, 0-1.</summary>
    public double Score { get; set; }
    /// <summary>The card's middle, in canvas units right of the screen's middle.</summary>
    public double X { get; set; }
    /// <summary>The read name's middle in frame pixels: where the card really is, to measure the UI's scale by.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double? NameX { get; set; }
}

/// <summary>Chromatic Inmates contained (at most 10).</summary>
public sealed class ChromaticRead
{
    public int Count { get; set; }
    public double Conf { get; set; }
    /// <summary>The game's UI (a tooltip) covers some slots: <see cref="Count"/> is only what shows.</summary>
    public bool Covered { get; set; }
}

/// <summary>Reads JSON null (JS NaN/Infinity after JSON.stringify) as NaN instead of throwing.</summary>
public sealed class LenientDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Null => double.NaN,
            JsonTokenType.True => 1,
            JsonTokenType.False => 0,
            _ => reader.GetDouble(),
        };

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsFinite(value)) writer.WriteNumberValue(value);
        else writer.WriteNullValue();
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    Converters = [typeof(LenientDoubleConverter), typeof(JsonStringEnumConverter<Code>), typeof(JsonStringEnumConverter<Rarity>)])]
[JsonSerializable(typeof(ScanResult))]
[JsonSerializable(typeof(SandboxLabels))]
[JsonSerializable(typeof(Dictionary<string, SandboxLabels>))]
[JsonSerializable(typeof(SampleMeta))]
public partial class VisionJson : JsonSerializerContext;
