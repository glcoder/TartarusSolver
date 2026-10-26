using System.Numerics;
using System.Runtime.InteropServices;
using Tartarus.Rules;

namespace Tartarus.Solver;

/// <summary>
/// The value network: a small multilayer perceptron (ReLU between layers) that reads a morning's state
/// (<see cref="Features"/>) and predicts how the run ends: utility first, then Chromatic, Ultimate, Legendary. Trained
/// by tools/train/valuenet.py on runs the search played. Plain C# with SIMD dot products: a few microseconds a call,
/// no runtime to load, AOT-safe. Thread-safe: each call brings its own scratch.
/// </summary>
public sealed class ValueNet
{
    private const int Magic = 0x544E5654; // "TVNT"
    private readonly float[][] _w;
    private readonly float[][] _b;
    private readonly int[] _in, _out;

    private ValueNet(float[][] w, float[][] b, int[] inputs, int[] outputs)
    {
        (_w, _b, _in, _out) = (w, b, inputs, outputs);
        if (_in[0] != Features.Count) throw new InvalidDataException($"value network reads {_in[0]} features, the encoder writes {Features.Count}");
        MaxWidth = Math.Max(_in.Max(), _out.Max());
    }

    public int MaxWidth { get; }
    public int Outputs => _out[^1];

    /// <summary>File: "TVNT", layer count, then per layer its outputs, inputs, weights (outputs x inputs, row by row) and biases, float32.</summary>
    public static ValueNet Load(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (r.ReadInt32() != Magic) throw new InvalidDataException($"{path} is not a value network");
        int layers = r.ReadInt32();
        var w = new float[layers][];
        var b = new float[layers][];
        var ins = new int[layers];
        var outs = new int[layers];
        for (int l = 0; l < layers; l++)
        {
            outs[l] = r.ReadInt32();
            ins[l] = r.ReadInt32();
            w[l] = new float[outs[l] * ins[l]];
            b[l] = new float[outs[l]];
            r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(w[l].AsSpan()));
            r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(b[l].AsSpan()));
        }
        return new ValueNet(w, b, ins, outs);
    }

    public static string? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "models", "solver", "value.bin");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The predicted utility of a state (<see cref="Utility"/> of its outputs).</summary>
    public double Value(in GameState s)
    {
        Span<float> x = stackalloc float[Features.Count];
        Features.Encode(s, x);
        Span<float> outputs = stackalloc float[Outputs];
        Forward(x, outputs);
        Bound(outputs, s);
        return Utility(outputs);
    }

    /// <summary>
    /// The counts kept to what can be: Chromatics no fewer than contained and no more than 10, Ultimate and Legendary
    /// not below none (a regression's guess strays a little past them).
    /// </summary>
    public static void Bound(Span<float> outputs, in GameState s)
    {
        if (outputs.Length < 4) return;
        outputs[1] = Math.Clamp(outputs[1], s.Chromatic, GameData.ChromaticCap);
        outputs[2] = Math.Max(0, outputs[2]);
        outputs[3] = Math.Max(0, outputs[3]);
    }

    /// <summary>
    /// The run's predicted worth from the outputs: the Chromatic, Ultimate and Legendary counts weighed as
    /// <see cref="RunScore.Utility"/> weighs them now (the first output learnt an older weighing); Rare and below are a
    /// rounding error. A network with one output: that output.
    /// </summary>
    public static double Utility(ReadOnlySpan<float> outputs) => outputs.Length >= 4
        ? outputs[1] + RunScore.Worth[4] * outputs[2] + RunScore.Worth[3] * outputs[3]
        : outputs[0];

    public void Forward(ReadOnlySpan<float> input, Span<float> output)
    {
        Span<float> a = stackalloc float[MaxWidth];
        Span<float> b = stackalloc float[MaxWidth];
        input.CopyTo(a);
        for (int l = 0; l < _w.Length; l++)
        {
            int n = _in[l], m = _out[l];
            var w = _w[l].AsSpan();
            var bias = _b[l];
            bool last = l == _w.Length - 1;
            for (int j = 0; j < m; j++)
            {
                float v = bias[j] + Dot(w.Slice(j * n, n), a[..n]);
                b[j] = last || v > 0 ? v : 0;
            }
            var t = a;
            a = b;
            b = t;
        }
        a[..output.Length].CopyTo(output);
    }

    private static float Dot(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        int i = 0;
        var acc = Vector<float>.Zero;
        int width = Vector<float>.Count;
        for (; i <= x.Length - width; i += width)
            acc += new Vector<float>(x.Slice(i, width)) * new Vector<float>(y.Slice(i, width));
        float sum = Vector.Dot(acc, Vector<float>.One);
        for (; i < x.Length; i++) sum += x[i] * y[i];
        return sum;
    }
}
