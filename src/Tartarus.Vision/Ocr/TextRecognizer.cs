using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Tartarus.Vision.Ocr;

/// <summary>
/// PP-OCR text line recognizer (SVTR/CRNN with CTC): a crop scaled to a fixed height in, one class
/// distribution per horizontal step out, decoded greedily. Class 0 is the CTC blank, then the
/// dictionary's characters, then a space.
/// </summary>
public sealed class TextRecognizer : IDisposable
{
    private static readonly float[] Half = [0.5f, 0.5f, 0.5f];

    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly string[] _classes;

    public TextRecognizer(string modelPath, string dictPath, SessionOptions options)
    {
        _session = new InferenceSession(modelPath, options);
        var meta = _session.InputMetadata.First();
        _input = meta.Key;
        int h = meta.Value.Dimensions.Length == 4 ? meta.Value.Dimensions[2] : -1;
        Height = h > 0 ? h : 48;
        var dict = File.ReadAllLines(dictPath).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);
        _classes = ["", .. dict, " "];
    }

    /// <summary>Input height the model was trained at.</summary>
    public int Height { get; }
    /// <summary>Narrowest batch width (PaddleOCR pads to 320).</summary>
    public int MinWidth { get; set; } = 320;
    public int MaxWidth { get; set; } = 1600;
    public int BatchSize { get; set; } = 8;

    public sealed record Result(string Text, float Conf, IReadOnlyList<TextChar> Chars);

    /// <summary>Reads each box of the frame as one line of text.</summary>
    public Result[] Recognize(Frame frame, IReadOnlyList<PixelBox> boxes)
    {
        var results = new Result[boxes.Count];
        // Similar widths together, so a batch pads little.
        var order = Enumerable.Range(0, boxes.Count).OrderBy(i => (double)boxes[i].Width / Math.Max(1, boxes[i].Height)).ToArray();
        for (int start = 0; start < order.Length; start += BatchSize)
        {
            var batch = order.AsSpan(start, Math.Min(BatchSize, order.Length - start)).ToArray();
            int[] widths = batch.Select(i => Math.Clamp((int)Math.Ceiling(Height * (double)boxes[i].Width / Math.Max(1, boxes[i].Height)), 8, MaxWidth)).ToArray();
            int batchW = Math.Max(MinWidth, widths.Max());
            var input = new DenseTensor<float>([batch.Length, 3, Height, batchW]);
            var buf = input.Buffer.Span;
            int itemSize = 3 * Height * batchW;
            for (int k = 0; k < batch.Length; k++)
            {
                var b = boxes[batch[k]];
                Resample.ToBgrPlanes(frame, b.X0, b.Y0, b.X1, b.Y1, widths[k], Height, buf.Slice(k * itemSize, itemSize), batchW, Half, Half);
            }
            using var outputs = _session.Run([NamedOnnxValue.CreateFromTensor(_input, input)]);
            var probs = outputs[0].AsTensor<float>();
            // [batch, steps, classes]
            int steps = probs.Dimensions[1], classes = probs.Dimensions[2];
            var flat = probs is DenseTensor<float> dense ? dense.Buffer.Span : probs.ToArray();
            double stepPx = (double)batchW / steps;
            for (int k = 0; k < batch.Length; k++)
            {
                var box = boxes[batch[k]];
                double toFrame = (double)box.Width / widths[k];
                results[batch[k]] = Decode(flat.Slice(k * steps * classes, steps * classes), steps, classes, step => box.X0 + step * stepPx * toFrame);
            }
        }
        return results;
    }

    private Result Decode(ReadOnlySpan<float> probs, int steps, int classes, Func<double, double> stepToX)
    {
        var chars = new List<TextChar>();
        var text = new System.Text.StringBuilder();
        int prev = 0, runStart = 0;
        float runBest = 0;
        for (int t = 0; t <= steps; t++)
        {
            int best = 0;
            float bestP = 0;
            if (t < steps)
            {
                var row = probs.Slice(t * classes, classes);
                for (int c = 0; c < classes; c++) if (row[c] > bestP) { bestP = row[c]; best = c; }
            }
            if (t < steps && best == prev)
            {
                runBest = Math.Max(runBest, bestP);
                continue;
            }
            // The run of `prev` ended at t.
            if (prev != 0 && prev < _classes.Length)
            {
                string s = _classes[prev];
                text.Append(s);
                foreach (char ch in s) chars.Add(new TextChar(ch, stepToX(runStart), stepToX(t), runBest));
            }
            prev = best;
            runStart = t;
            runBest = bestP;
        }
        float conf = chars.Count == 0 ? 0 : chars.Average(c => c.Conf);
        return new Result(text.ToString().Trim(), conf, chars);
    }

    public void Dispose() => _session.Dispose();
}
