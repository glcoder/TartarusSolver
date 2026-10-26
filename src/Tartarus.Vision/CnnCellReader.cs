using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Tartarus.Vision;

/// <summary>
/// Reads the cells with the networks tools/train/cellnet.py trains (models/cells): each cell 2-16 with its
/// surroundings (<see cref="CellCrops.Context"/>) through cellnet, each cell's arrow spot
/// (<see cref="CellCrops.ArrowSpot"/>) through arrownet. The crops are cut by the same code as the training
/// crops. The instrument and Chromatic panels come from <paramref name="panels"/> until they have
/// classifiers of their own.
/// </summary>
public sealed class CnnCellReader : ICellReader
{
    private readonly InferenceSession _cells, _arrows;
    private readonly string[] _kinds, _codes;
    /// <summary>Indices of the flags in the network's flag output, -1 for one it lacks or wasn't trained enough on.</summary>
    private readonly int _consumable, _nest, _incident, _scratch, _tusk, _prey;
    private readonly ICellReader? _panels;

    public CnnCellReader(string? modelDir = null, ICellReader? panels = null, int threads = 2)
    {
        ModelDir = modelDir ?? FindModelDir() ?? throw new DirectoryNotFoundException("Cell models not found (models/cells/cellnet.onnx).");
        using var options = new SessionOptions { IntraOpNumThreads = threads, InterOpNumThreads = 1 };
        _cells = new InferenceSession(Path.Combine(ModelDir, "cellnet.onnx"), options);
        _arrows = new InferenceSession(Path.Combine(ModelDir, "arrownet.onnx"), options);
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModelDir, "cellnet.json")));
        string[] Names(string key) => meta.RootElement.GetProperty(key).EnumerateArray().Select(e => e.GetString()!).ToArray();
        _kinds = Names("kinds");
        _codes = Names("codes");
        // Flags by name; one trained on too few examples (flagCounts under minFlagExamples) isn't read.
        var flagNames = Names("flags");
        var root = meta.RootElement;
        int minExamples = root.TryGetProperty("minFlagExamples", out var me) ? me.GetInt32() : 0;
        int Flag(string name)
        {
            int i = Array.IndexOf(flagNames, name);
            if (i < 0) return -1;
            if (root.TryGetProperty("flagCounts", out var counts) && counts.TryGetProperty(name, out var n) && n.GetInt32() < minExamples) return -1;
            return i;
        }
        (_consumable, _nest, _incident, _scratch, _tusk, _prey) = (Flag("consumable"), Flag("nest"), Flag("incident"), Flag("scratch_marks"), Flag("tusk"), Flag("prey"));
        Trained = meta.RootElement.TryGetProperty("trained", out var t) ? t.GetString() : null;
        _panels = panels;
    }

    public string Name => "cnn" + (Trained != null ? $" ({Trained})" : "") + (_panels != null ? " + " + _panels.Name + " panels" : "");
    public string ModelDir { get; }
    public string? Trained { get; }

    public static string? FindModelDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "models", "cells");
            if (File.Exists(Path.Combine(candidate, "cellnet.onnx")) && File.Exists(Path.Combine(candidate, "arrownet.onnx"))) return candidate;
        }
        return null;
    }

    public ScanResult Read(Frame frame, ScreenLayout layout)
    {
        var sw = Stopwatch.StartNew();
        var loc = layout.Sandbox;
        // Panels first on another thread: they don't depend on the cells.
        var panels = _panels == null ? null : Task.Run(() => _panels.Read(frame, layout));

        var places = Enumerable.Range(0, 16).Select(n => layout.Cell(n + 1)).ToArray();
        var cellInput = new DenseTensor<float>([15, 3, CellCrops.ContextHeight, CellCrops.ContextWidth]);
        var cellIds = new DenseTensor<float>([15, 16]);
        for (int i = 1; i < 16; i++)
        {
            var (x, y, k) = places[i];
            Fill(cellInput, i - 1, CellCrops.Context(frame, x, y, k));
            cellIds[i - 1, i] = 1;
        }
        var arrowInput = new DenseTensor<float>([16, 3, CellCrops.ArrowSize, CellCrops.ArrowSize]);
        var arrowIds = new DenseTensor<float>([16, 16]);
        for (int i = 0; i < 16; i++)
        {
            Fill(arrowInput, i, CellCrops.ArrowSpot(frame, loc, i));
            arrowIds[i, i] = 1;
        }
        double cropMs = sw.Elapsed.TotalMilliseconds;

        using var cellOut = _cells.Run([NamedOnnxValue.CreateFromTensor("image", cellInput), NamedOnnxValue.CreateFromTensor("cell", cellIds)]);
        using var arrowOut = _arrows.Run([NamedOnnxValue.CreateFromTensor("image", arrowInput), NamedOnnxValue.CreateFromTensor("cell", arrowIds)]);
        var kind = Output(cellOut, "kind");
        var code = Output(cellOut, "code");
        var rarity = Output(cellOut, "rarity");
        var visible = Output(cellOut, "visible");
        var flags = Output(cellOut, "flags");
        var arrow = Output(arrowOut, "arrow");

        var cells = new List<CellRead>(16)
        {
            new() { N = 0, Kind = CellRead.InterrogationRoom, X = places[0].X, Y = places[0].Y, K = places[0].K, Conf = 1 },
        };
        for (int i = 1; i < 16; i++)
        {
            int j = i - 1;
            var (kIdx, kP) = ArgMax(Softmax(kind[j]));
            var c = new CellRead { N = i, Kind = _kinds[kIdx], X = places[i].X, Y = places[i].Y, K = places[i].K, Conf = kP };
            var f = flags[j].Select(Sigmoid).ToArray();
            bool F(int index) => index >= 0 && f[index] > 0.5;
            c.Incident = c.Kind != CellRead.Empty && F(_incident);
            if (c.Kind == CellRead.Inmate)
            {
                var (cIdx, cP) = ArgMax(Softmax(code[j]));
                var (rIdx, rP) = ArgMax(Softmax(rarity[j]));
                c.Code = Codes.Parse(_codes[cIdx]);
                c.Rarity = (Rarity)rIdx;
                c.Consumable = F(_consumable);
                c.Tusk = F(_tusk);
                c.Prey = F(_prey);
                c.Conf = Math.Min(kP, Math.Min(cP, rP));
            }
            else if (c.Kind == CellRead.Unopened)
            {
                c.Nest = F(_nest);
                // visible: "none", then the rarities.
                var (vIdx, vP) = ArgMax(Softmax(visible[j]));
                if (vIdx > 0) c.VisibleRarity = new VisibleRarityRead { Rarity = (Rarity)(vIdx - 1), ScratchMarks = F(_scratch), Conf = vP };
            }
            cells.Add(c);
        }

        // The warden: the arrow spot that looks most like the arrow, if any does.
        var arrowLogits = arrow.Select(a => a[0]).ToArray();
        var (wIdx, _) = ArgMax(arrowLogits);
        var r = new ScanResult { Ok = true, Loc = loc, Cells = cells };
        if (arrowLogits[wIdx] > 0) r.Warden = new WardenRead { Cell = wIdx, Conf = Sigmoid(arrowLogits[wIdx]) };

        if (panels != null)
        {
            var p = panels.GetAwaiter().GetResult();
            r.Instruments = p.Instruments;
            r.Chromatic = p.Chromatic;
            foreach (var (key, ms) in p.Timings) r.Timings["panels." + key] = ms;
        }
        r.Timings["cnn.crop"] = cropMs;
        r.Timings["cnn"] = sw.Elapsed.TotalMilliseconds;
        r.Ms = sw.Elapsed.TotalMilliseconds;
        return r;
    }

    // RGB in 0..1, NCHW, as tools/train/cellnet.py loads the PNG crops.
    internal static void Fill(DenseTensor<float> t, int n, Frame crop)
    {
        int h = crop.Height, w = crop.Width;
        int r = crop.Format == PixelFormat.Bgra32 ? 2 : 0, b = 2 - r;
        var p = crop.Pixels;
        var buf = t.Buffer.Span;
        int plane = h * w, item = 3 * plane;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4, d = n * item + y * w + x;
                buf[d] = p[o + r] / 255f;
                buf[d + plane] = p[o + 1] / 255f;
                buf[d + 2 * plane] = p[o + b] / 255f;
            }
    }

    // An [n, classes] output as one array per row.
    private static float[][] Output(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs, string name)
    {
        var t = outputs.First(o => o.Name == name).AsTensor<float>();
        int n = t.Dimensions[0], c = t.Dimensions[1];
        var flat = t.ToArray();
        return Enumerable.Range(0, n).Select(i => flat.AsSpan(i * c, c).ToArray()).ToArray();
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    private static float[] Softmax(float[] logits)
    {
        float max = logits.Max();
        var e = logits.Select(v => MathF.Exp(v - max)).ToArray();
        float sum = e.Sum();
        return e.Select(v => v / sum).ToArray();
    }

    private static (int Index, float Value) ArgMax(float[] values)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++) if (values[i] > values[best]) best = i;
        return (best, values[best]);
    }

    public void Dispose()
    {
        _cells.Dispose();
        _arrows.Dispose();
        _panels?.Dispose();
    }
}
