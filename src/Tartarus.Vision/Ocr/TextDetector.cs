using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Tartarus.Vision.Ocr;

/// <summary>
/// PP-OCR text detector (DBNet): a per-pixel text probability map, cut into regions. Game text is
/// horizontal, so regions are axis-aligned boxes rather than PaddleOCR's rotated quads.
/// </summary>
public sealed class TextDetector : IDisposable
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly InferenceSession _session;
    private readonly string _input;

    public TextDetector(string modelPath, SessionOptions options)
    {
        _session = new InferenceSession(modelPath, options);
        _input = _session.InputMetadata.Keys.First();
    }

    /// <summary>The longer side of the image the detector sees; larger finds smaller text, slower.</summary>
    public int LimitSide { get; set; } = 960;
    /// <summary>Pixels above this probability are text.</summary>
    public float Threshold { get; set; } = 0.3f;
    /// <summary>A region is kept if its mean probability is at least this.</summary>
    public float BoxThreshold { get; set; } = 0.5f;
    /// <summary>DBNet predicts shrunk text; regions grow back by area * ratio / perimeter.</summary>
    public float UnclipRatio { get; set; } = 1.6f;

    public sealed record Region(PixelBox Box, float Score);

    /// <summary>Text regions in <paramref name="area"/> of the frame (all of it by default), in frame pixels.</summary>
    public List<Region> Detect(Frame frame, PixelBox? area = null)
    {
        var a = (area ?? new PixelBox(0, 0, frame.Width, frame.Height)).Clamp(frame.Width, frame.Height);
        if (a.Width < 8 || a.Height < 8) return [];
        double ratio = Math.Min(1.0, (double)LimitSide / Math.Max(a.Width, a.Height));
        // The network wants sides in multiples of 32: the picture keeps its proportions and the rest is
        // padding (zero after normalizing, the mean color), so a thin strip of text isn't squashed.
        int w = Math.Max(1, (int)Math.Round(a.Width * ratio)), h = Math.Max(1, (int)Math.Round(a.Height * ratio));
        int tw = Math.Max(32, (w + 31) / 32 * 32), th = Math.Max(32, (h + 31) / 32 * 32);

        var input = new DenseTensor<float>([1, 3, th, tw]);
        Resample.ToBgrPlanes(frame, a.X0, a.Y0, a.X1, a.Y1, w, h, input.Buffer.Span, tw, Mean, Std, th);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_input, input)]);
        var prob = results[0].AsTensor<float>();
        // Output is [1, 1, h, w].
        var map = prob is DenseTensor<float> dense ? dense.Buffer.Span : prob.ToArray();
        return Regions(map, tw, th, a, (double)a.Width / w, (double)a.Height / h);
    }

    private List<Region> Regions(ReadOnlySpan<float> map, int w, int h, PixelBox area, double kx, double ky)
    {
        var label = new int[w * h];
        var stack = new Stack<int>();
        var regions = new List<Region>();
        int next = 0;
        for (int start = 0; start < w * h; start++)
        {
            if (label[start] != 0 || map[start] <= Threshold) continue;
            next++;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
            double sum = 0;
            label[start] = next;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % w, y = i / w;
                count++;
                sum += map[i];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                for (int k = 0; k < 4; k++)
                {
                    int j = k switch { 0 => x > 0 ? i - 1 : -1, 1 => x < w - 1 ? i + 1 : -1, 2 => y > 0 ? i - w : -1, _ => y < h - 1 ? i + w : -1 };
                    if (j < 0 || label[j] != 0 || map[j] <= Threshold) continue;
                    label[j] = next;
                    stack.Push(j);
                }
            }
            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (Math.Min(bw, bh) < 3) continue;
            float score = (float)(sum / count);
            if (score < BoxThreshold) continue;
            double d = bw * bh * UnclipRatio / (2.0 * (bw + bh));
            var box = new PixelBox(
                area.X0 + (int)Math.Floor((minX - d) * kx),
                area.Y0 + (int)Math.Floor((minY - d) * ky),
                area.X0 + (int)Math.Ceiling((maxX + 1 + d) * kx),
                area.Y0 + (int)Math.Ceiling((maxY + 1 + d) * ky)).Clamp(area.X1, area.Y1);
            regions.Add(new Region(box, score));
        }
        return regions;
    }

    public void Dispose() => _session.Dispose();
}
