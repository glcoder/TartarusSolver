using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Tartarus.Vision;

/// <summary>
/// Reads the screen's panels with the networks tools/train/slotnet.py trains (models/slots): how many Chromatic
/// Prisoner slots are filled, and which Torture Instrument each instrument slot holds. The slots are cut by
/// <see cref="ScreenLayout.Crop"/>, as the training crops are. Returns a partial <see cref="ScanResult"/> (Instruments,
/// Chromatic), for <see cref="CnnCellReader"/>'s panels.
/// </summary>
public sealed class SlotReader : ICellReader
{
    private readonly InferenceSession _chromatic, _instrument;
    private readonly string[] _chromaticClasses, _instrumentClasses;
    /// <summary>The instrument network's "hidden" class (a slot under a tooltip), or -1 for a network without one.</summary>
    private readonly int _hiddenClass;

    public SlotReader(string? modelDir = null, int threads = 1)
    {
        ModelDir = modelDir ?? FindModelDir() ?? throw new DirectoryNotFoundException("Slot models not found (models/slots/chromatic.onnx).");
        using var options = new SessionOptions { IntraOpNumThreads = threads, InterOpNumThreads = 1 };
        _chromatic = new InferenceSession(Path.Combine(ModelDir, "chromatic.onnx"), options);
        _instrument = new InferenceSession(Path.Combine(ModelDir, "instrument.onnx"), options);
        _chromaticClasses = Classes("chromatic");
        _instrumentClasses = Classes("instrument");
        _hiddenClass = Array.IndexOf(_instrumentClasses, "hidden");

        string[] Classes(string name)
        {
            using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModelDir, name + ".json")));
            return meta.RootElement.GetProperty("classes").EnumerateArray().Select(e => e.GetString()!).ToArray();
        }
    }

    /// <summary>The id of an instrument the network doesn't know.</summary>
    public const string Unknown = "?";
    /// <summary>The id of a slot the game's UI (a tooltip) covers: there is no instrument to read in it now.</summary>
    public const string Hidden = "hidden";

    /// <summary>Whether a slot read names an instrument (not <see cref="Unknown"/> or <see cref="Hidden"/>).</summary>
    public static bool IsInstrument(string id) => id != Unknown && id != Hidden;
    /// <summary>Least probability of an instrument read; below it the slot holds an instrument the network doesn't know.</summary>
    public const float MinConf = 0.8f;

    public string Name => "slots";
    public string ModelDir { get; }
    /// <summary>The instruments the network knows (others read as the closest of these).</summary>
    public IReadOnlyList<string> KnownInstruments => _instrumentClasses.Skip(1).Where(c => c != "hidden").ToArray();

    public static string? FindModelDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "models", "slots");
            if (File.Exists(Path.Combine(candidate, "chromatic.onnx")) && File.Exists(Path.Combine(candidate, "instrument.onnx"))) return candidate;
        }
        return null;
    }

    public ScanResult Read(Frame frame, ScreenLayout layout)
    {
        var sw = Stopwatch.StartNew();
        var chromatic = Classify(_chromatic, Enumerable.Range(0, ScreenLayout.ChromaticSlots).Select(i => layout.Crop(frame, ScreenLayout.ChromaticSlot(i))).ToList());
        var instruments = Classify(_instrument, Enumerable.Range(0, ScreenLayout.InstrumentSlots).Select(i => layout.Crop(frame, ScreenLayout.InstrumentSlot(i))).ToList());

        int filled = chromatic.Count(c => _chromaticClasses[c.Class] == "filled");
        bool covered = chromatic.Any(c => _chromaticClasses[c.Class] == "hidden");
        var held = new List<InstrumentRead>();
        // Instruments fill the slots from the left: the first empty one ends the row. A slot a tooltip covers reads
        // Hidden; one the network isn't sure of, Unknown (the app shows what it read there before).
        foreach (var (cls, p) in instruments)
        {
            if (cls == 0 && p >= MinConf) break;
            string id = cls == _hiddenClass ? Hidden : p >= MinConf ? _instrumentClasses[cls] : Unknown;
            held.Add(new InstrumentRead { Id = id, Conf = p, Score = p });
        }
        var r = new ScanResult
        {
            Ok = true,
            Chromatic = new ChromaticRead { Count = filled, Conf = chromatic.Min(c => c.P), Covered = covered },
            Instruments = new InstrumentsRead { Found = true, Instruments = held },
        };
        r.Timings["slots"] = sw.Elapsed.TotalMilliseconds;
        return r;
    }

    private static List<(int Class, float P)> Classify(InferenceSession session, List<Frame> crops)
    {
        var first = crops[0];
        var input = new DenseTensor<float>([crops.Count, 3, first.Height, first.Width]);
        for (int i = 0; i < crops.Count; i++) CnnCellReader.Fill(input, i, crops[i]);
        using var output = session.Run([NamedOnnxValue.CreateFromTensor("image", input)]);
        var t = output[0].AsTensor<float>();
        int classes = t.Dimensions[1];
        var flat = t.ToArray();
        var result = new List<(int, float)>(crops.Count);
        for (int i = 0; i < crops.Count; i++)
        {
            var row = flat.AsSpan(i * classes, classes);
            float max = float.MinValue;
            foreach (var v in row) max = Math.Max(max, v);
            float sum = 0;
            int best = 0;
            for (int c = 0; c < classes; c++)
            {
                sum += MathF.Exp(row[c] - max);
                if (row[c] > row[best]) best = c;
            }
            result.Add((best, 1f / sum));
        }
        return result;
    }

    public void Dispose()
    {
        _chromatic.Dispose();
        _instrument.Dispose();
    }
}
