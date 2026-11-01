using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;

namespace Tartarus.Vision.Ocr;

/// <summary>
/// Detection plus recognition: every line of text in a frame (or a part of it). The detector finds text in any
/// script; the recognizer is the game language's (<see cref="OcrPacks"/>), English until told otherwise.
/// </summary>
public sealed class OcrEngine : IDisposable
{
    public const string DetectorFile = "det.onnx";
    public const string RecognizerFile = "rec_en.onnx";
    public const string DictionaryFile = "dict_en.txt";

    private readonly SessionOptions _options;
    private readonly Dictionary<string, TextRecognizer> _recognizers = [];
    private volatile string? _wantedLanguage;
    private volatile bool _languageAsked;

    /// <summary>
    /// How every network session runs. The runtime's worker threads sleep between operations instead of spinning for
    /// the next one: a scan took ~2 s of CPU for 0.27 s of time (2026-11-01, off the board every 5 s still half a core),
    /// the spinning taken from the game. And memory: one arena for all sessions that grows by what is asked, not by
    /// doubling, without memory patterns (OCR's inputs change size every read, so patterns never fit): the arena kept
    /// growing through a run (working set 510 MB before it, 1.27 GB at its peak). Measured on 220 frames: peak working
    /// set 530-541 -> 387-398 MB, private 591-601 -> 364-377 MB; on a run's 56 frames scanned three times each, 300 MB
    /// against 430-500, the scans as fast (fresh ~175 ms, cached ~45 ms either way); without any arena 320 MB but twice
    /// as slow.
    /// </summary>
    public static void Tune(SessionOptions options)
    {
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        SharedArena();
        options.AddSessionConfigEntry("session.use_env_allocators", "1");
        options.EnableMemoryPattern = false;
    }

    private static readonly object ArenaLock = new();
    private static bool _arenaRegistered;

    /// <summary>The process's one CPU arena, extended by the size asked for (kSameAsRequested), registered once.</summary>
    private static void SharedArena()
    {
        lock (ArenaLock)
        {
            if (_arenaRegistered) return;
            using var memory = new OrtMemoryInfo(OrtMemoryInfo.allocatorCPU, OrtAllocatorType.ArenaAllocator, 0, OrtMemType.Default);
            using var config = new OrtArenaCfg(0, 1, -1, -1);
            OrtEnv.Instance().CreateAndRegisterAllocator(memory, config);
            _arenaRegistered = true;
        }
    }

    public OcrEngine(string? modelDir = null, int threads = 4, string? gameLanguage = null)
    {
        ModelDir = modelDir ?? FindModelDir();
        _options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        Tune(_options);
        Detector = new TextDetector(Path.Combine(ModelDir, DetectorFile), _options);
        Recognizer = _recognizers[OcrPacks.English.Id] = new TextRecognizer(Path.Combine(ModelDir, RecognizerFile), Path.Combine(ModelDir, DictionaryFile), _options);
        Pack = OcrPacks.English;
        if (gameLanguage != null)
        {
            RequestLanguage(gameLanguage);
            ApplyLanguage();
        }
    }

    public string ModelDir { get; }
    public TextDetector Detector { get; }
    public TextRecognizer Recognizer { get; private set; }
    /// <summary>The recognizer reading now.</summary>
    public OcrPack Pack { get; private set; }
    /// <summary>The game language asked for last (its code), whether or not its pack was there.</summary>
    public string? GameLanguage { get; private set; }

    /// <summary>Asks for the recognizer of a game language (any thread); it takes over at <see cref="ApplyLanguage"/>.</summary>
    public void RequestLanguage(string? gameLanguage)
    {
        _wantedLanguage = gameLanguage ?? "en";
        _languageAsked = true;
    }

    /// <summary>
    /// Switches to the recognizer asked for, on the reading thread between frames: loaded once, kept. A pack not
    /// installed (models/ocr, or downloaded) leaves English until it is and the language is asked for again. Returns
    /// true when the recognizer changed.
    /// </summary>
    public bool ApplyLanguage()
    {
        if (!_languageAsked || _wantedLanguage is not { } wanted) return false;
        _languageAsked = false;
        GameLanguage = wanted;
        var pack = OcrPacks.ForGame(wanted);
        string? dir = OcrPacks.Locate(pack, ModelDir);
        if (dir == null) (pack, dir) = (OcrPacks.English, ModelDir);
        if (pack == Pack) return false;
        if (!_recognizers.TryGetValue(pack.Id, out var recognizer))
            recognizer = _recognizers[pack.Id] = new TextRecognizer(Path.Combine(dir, pack.Model), Path.Combine(dir, pack.Dictionary), _options);
        (Recognizer, Pack) = (recognizer, pack);
        // What was read with the other alphabet is no reading to keep.
        _areas.Clear();
        return true;
    }
    /// <summary>Lines read with less confidence than this are dropped.</summary>
    public float MinConf { get; set; } = 0.5f;

    public static bool ModelsExist(string dir) =>
        File.Exists(Path.Combine(dir, DetectorFile)) && File.Exists(Path.Combine(dir, RecognizerFile)) && File.Exists(Path.Combine(dir, DictionaryFile));

    /// <summary>models/ocr next to the executable, else in the repository it runs from.</summary>
    public static string FindModelDir()
    {
        foreach (string dir in AppFiles.Folders)
        {
            string candidate = Path.Combine(dir, "models", "ocr");
            if (ModelsExist(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("OCR models not found (expected models/ocr with det.onnx, rec_en.onnx, dict_en.txt).");
    }

    public sealed record Timing(double DetectMs, double RecognizeMs, int Regions, int Recognized, bool Reused = false);

    public Timing LastTiming { get; private set; } = new(0, 0, 0, 0);

    /// <summary>
    /// Reuse earlier readings where the picture hasn't changed: a whole area that looks as it did last time
    /// keeps its lines without detection, and a line whose region looks the same keeps its text. Most of the
    /// screen's text holds still between frames. Not thread-safe: one reader at a time.
    /// </summary>
    public bool ReuseUnchanged { get; set; } = true;

    private sealed record Cached(PixelBox Box, Thumb Thumb, TextRecognizer.Result Result);
    private sealed class AreaCache
    {
        public Thumb Thumb = null!;
        public List<Cached> Regions = [];
        public List<TextLine> Lines = [];
    }
    private readonly Dictionary<PixelBox, AreaCache> _areas = [];

    /// <summary>Lines in reading order (top to bottom, then left to right).</summary>
    public List<TextLine> Read(Frame frame, PixelBox? area = null)
    {
        var sw = Stopwatch.StartNew();
        var key = (area ?? new PixelBox(0, 0, frame.Width, frame.Height)).Clamp(frame.Width, frame.Height);
        var areaThumb = Thumb.Of(frame, key);
        _areas.TryGetValue(key, out var cache);
        if (ReuseUnchanged && cache != null && cache.Thumb.Same(areaThumb))
        {
            LastTiming = new Timing(0, sw.Elapsed.TotalMilliseconds, cache.Regions.Count, 0, Reused: true);
            return cache.Lines;
        }

        var regions = Detector.Detect(frame, key);
        double det = sw.Elapsed.TotalMilliseconds;

        var read = new TextRecognizer.Result[regions.Count];
        var thumbs = new Thumb[regions.Count];
        var todo = new List<int>();
        for (int i = 0; i < regions.Count; i++)
        {
            thumbs[i] = Thumb.Of(frame, regions[i].Box);
            var hit = ReuseUnchanged && cache != null ? cache.Regions.FirstOrDefault(c => Near(c.Box, regions[i].Box) && c.Thumb.Same(thumbs[i])) : null;
            if (hit == null) todo.Add(i);
            else
            {
                double dx = regions[i].Box.X0 - hit.Box.X0;
                read[i] = hit.Result with { Chars = hit.Result.Chars.Select(c => c with { X0 = c.X0 + dx, X1 = c.X1 + dx }).ToArray() };
            }
        }
        var fresh = Recognizer.Recognize(frame, todo.Select(i => regions[i].Box).ToArray());
        for (int k = 0; k < todo.Count; k++) read[todo[k]] = fresh[k];
        LastTiming = new Timing(det, sw.Elapsed.TotalMilliseconds - det, regions.Count, todo.Count);

        var lines = new List<TextLine>();
        for (int i = 0; i < regions.Count; i++)
        {
            var r = read[i];
            if (r.Text.Length == 0 || r.Conf < MinConf) continue;
            var box = regions[i].Box;
            var ink = InkBounds.Find(frame, box);
            lines.Add(new TextLine { Text = r.Text, Box = box, Ink = ink, Conf = r.Conf, DetScore = regions[i].Score, Chars = r.Chars });
        }
        // Rows: lines whose middles are within half a line height of the row's first line.
        lines.Sort((a, b) => a.Box.CenterY.CompareTo(b.Box.CenterY));
        var ordered = new List<TextLine>(lines.Count);
        for (int i = 0; i < lines.Count;)
        {
            int j = i + 1;
            while (j < lines.Count && lines[j].Box.CenterY - lines[i].Box.CenterY < lines[i].Box.Height / 2.0) j++;
            ordered.AddRange(lines.GetRange(i, j - i).OrderBy(l => l.Box.X0));
            i = j;
        }
        _areas[key] = new AreaCache { Thumb = areaThumb, Regions = regions.Select((r, i) => new Cached(r.Box, thumbs[i], read[i])).ToList(), Lines = ordered };
        return ordered;
    }

    private static bool Near(PixelBox a, PixelBox b) =>
        Math.Abs(a.X0 - b.X0) <= 2 && Math.Abs(a.X1 - b.X1) <= 2 && Math.Abs(a.Y0 - b.Y0) <= 2 && Math.Abs(a.Y1 - b.Y1) <= 2;

    public void Dispose()
    {
        Detector.Dispose();
        foreach (var r in _recognizers.Values) r.Dispose();
        _options.Dispose();
    }
}
