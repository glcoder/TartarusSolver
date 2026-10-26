using Tartarus.Vision;
using Tartarus.Vision.Ocr;

/// <summary>
/// tartarus-scan card-templates &lt;dataset&gt; [--before id] [--max N] [--out dir]: the card icons' templates
/// (models/cards) from English frames, each card's kind told by its words; then how well they tell the cards apart,
/// Destinations to cells included.
/// </summary>
static class CardTemplates
{
    public static int Run(string[] args, Func<string[], ISandboxScanner> makeScanner, Func<string[], string, string?> opt)
    {
        var ds = new Dataset(args[0]);
        string? before = opt(args, "--before");
        int max = int.TryParse(opt(args, "--max"), out var m) ? m : 40;
        string outDir = opt(args, "--out") ?? CardIcons.FindDir() ?? Path.Combine("models", "cards");
        using var scanner = makeScanner(args);

        var samples = CardIcons.Kinds.Append("cell").ToDictionary(k => k, _ => new List<float[]>());
        int width = CardIcons.Size + 2 * CardIcons.Search, height = CardIcons.Size + 2 * CardIcons.SearchY;
        foreach (var file in Directory.EnumerateFiles(ds.FramesDir, "*.png").OrderByDescending(f => f, StringComparer.Ordinal))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            if (before != null && string.CompareOrdinal(id, before) >= 0) continue;
            if (samples.Values.All(s => s.Count >= max)) break;
            if (new FileInfo(file).Length == 0) continue;
            var frame = ImageIO.Load(file);
            var r = scanner.Scan(frame);
            if (!r.Ok || r.Text is not { Cards.Count: > 0 } text || r.Layout is not { } layout) continue;
            foreach (var card in text.Cards)
            {
                string? kind = KindOf(ScreenText.Normalize(card.Text), card.DestinationCell);
                if (kind == null || samples[kind].Count >= max) continue;
                double cx = (card.Line.Box.CenterX - layout.Width / 2.0) / layout.K;
                samples[kind].Add(CardIcons.Rgb(CardIcons.Patch(frame, layout, cx)));
            }
        }
        Console.WriteLine("samples: " + string.Join(", ", samples.Select(s => $"{s.Key} {s.Value.Count}")));

        Directory.CreateDirectory(outDir);
        var templates = new Dictionary<string, float[]>();
        foreach (var kind in CardIcons.Kinds)
        {
            if (samples[kind].Count < 2) continue;
            var mean = Average(samples[kind], width, height);
            templates[kind] = CardIcons.Normalized(mean);
            ImageIO.SavePng(ToFrame(mean), Path.Combine(outDir, kind + ".png"));
        }
        Console.WriteLine($"templates in {outDir}: {string.Join(", ", templates.Keys)}");

        // Each sample against every template: the best kind (or none under the threshold) vs its words.
        foreach (var (truth, list) in samples)
        {
            var told = new Dictionary<string, int>();
            double worst = 1;
            foreach (var rgb in list)
            {
                var scores = templates.ToDictionary(t => t.Key, t => CardIcons.BestMatch(rgb, width, height, t.Value).Score);
                var best = scores.MaxBy(s => s.Value);
                string got = best.Value >= CardIcons.Threshold ? best.Key : "cell";
                told[got] = told.GetValueOrDefault(got) + 1;
                if (got == truth) worst = Math.Min(worst, truth == "cell" ? 1 - best.Value : best.Value);
            }
            Console.WriteLine($"{truth,-10} → {string.Join(", ", told.OrderByDescending(t => t.Value).Select(t => $"{t.Key} {t.Value}"))}  (closest call {worst:0.00})");
        }
        return 0;
    }

    private static string? KindOf(string t, int? cell)
    {
        if (t.Contains("interrogation")) return "room";
        if (cell != null && t.StartsWith("go to", StringComparison.Ordinal)) return "cell";
        if (t.Contains("consume")) return "consume";
        if (t.Contains("hunt")) return "hunt";
        if (t.Contains("replace")) return "replace";
        if (t.Contains("instigate")) return "instigate";
        if (t.Contains("bestow")) return "bestow";
        if (t == "end" || t.EndsWith(" end", StringComparison.Ordinal)) return "end";
        return null;
    }

    /// <summary>The samples aligned on one another (a card under the pointer sits higher) and averaged, Size² RGB.</summary>
    private static float[] Average(List<float[]> samples, int width, int height)
    {
        int n = CardIcons.Size * CardIcons.Size * 3;
        float[] Cut(float[] rgb, int ox, int oy)
        {
            var t = new float[n];
            for (int y = 0; y < CardIcons.Size; y++)
                Array.Copy(rgb, ((oy + y) * width + ox) * 3, t, y * CardIcons.Size * 3, CardIcons.Size * 3);
            return t;
        }
        var mean = Cut(samples[0], CardIcons.Search, CardIcons.SearchY);
        for (int round = 0; round < 3; round++)
        {
            var reference = CardIcons.Normalized(mean);
            var sum = new double[n];
            foreach (var rgb in samples)
            {
                var (_, dx, dy) = CardIcons.BestMatch(rgb, width, height, reference);
                var cut = Cut(rgb, dx, dy);
                for (int i = 0; i < n; i++) sum[i] += cut[i];
            }
            mean = sum.Select(v => (float)(v / samples.Count)).ToArray();
        }
        return mean;
    }

    private static Frame ToFrame(float[] rgb)
    {
        var px = new byte[CardIcons.Size * CardIcons.Size * 4];
        for (int i = 0; i < CardIcons.Size * CardIcons.Size; i++)
        {
            for (int ch = 0; ch < 3; ch++) px[i * 4 + ch] = (byte)Math.Clamp((int)Math.Round(rgb[i * 3 + ch] * 255), 0, 255);
            px[i * 4 + 3] = 255;
        }
        return new Frame(CardIcons.Size, CardIcons.Size, px, PixelFormat.Rgba32);
    }
}
