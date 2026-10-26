using System.Diagnostics;
using Tartarus.Capture;
using Tartarus.Vision;
using Tartarus.Vision.Ocr;

// tartarus-scan: runs the screenshot reader on files, outside the game.
//
//   tartarus-scan scan <file|dir> [--truth truth.json] [--out dir] [--repeat N] [--quiet]
//   tartarus-scan export <dataset dir> [--out dir]   cut training crops (cells, arrow spots, slots) + labels
//   tartarus-scan sandbox-template <dataset dir>     build the Sandbox presence template (models/sandbox) from the frames
//   tartarus-scan migrate <dataset dir>              rewrite truth.json in the game's terms (keeps truth.json.bak)
//   tartarus-scan review <dataset dir> [--all] [--red] [--mouth] [--covered] [--marks] [--found] [--suspect] [--port 8765] [--no-serve]
//                                                    the cell networks against the labels: a local page to settle disagreements
//   tartarus-scan windows                    list capturable top-level windows
//   tartarus-scan capture [title regex] [--out file.png]   grab one frame (default: the game) and scan it
//   tartarus-scan ocr <file|dir> [--repeat N] [--side px]   every line of text the OCR finds, with timings
//   tartarus-scan sim <frame.png> [--seconds S]   the frame into the rules: the move hint (actions ranked by the search)
//   tartarus-scan carry-red <dataset dir> [--dry]   label the Consume pick's red frames from the plain frame before them
//
// scan: prints what was read; with labels (--truth, else <dir>/truth.json or <dir>/../truth.json)
// it scores the reads like tartarus-simulator/tools/scan-test.js. --out saves the raw scan JSON per file.

Console.OutputEncoding = System.Text.Encoding.UTF8;
Win32Windows.EnableDpiAwareness();

return args.Length == 0 ? Usage() : args[0] switch
{
    "scan" => Scan(args[1..]),
    "export" => Export(args[1..]),
    "sandbox-template" => SandboxTemplate(args[1..]),
    "migrate" => Migrate(args[1..]),
    "review" => args.Length > 1 ? Review.Run(args[1..], Opt) : Usage(),
    "windows" => Windows(),
    "capture" => Capture(args[1..]),
    "ocr" => Ocr(args[1..]),
    "sim" => Sim.Run(args[1..], Opt, MakeScanner),
    "runs" => LogRuns.Run(args[1..], Opt),
    "carry-red" => CarryRed.Run(args[1..], MakeScanner),
    "audit" => Audit.Run(args[1..]),
    "ocr-get" => args.Length > 1 ? OcrGet(args[1]) : Usage(),
    "card-templates" => args.Length > 1 ? CardTemplates.Run(args[1..], MakeScanner, Opt) : Usage(),
    _ => Usage(),
};

static int Windows()
{
    foreach (var w in Win32Windows.List()) Console.WriteLine($"0x{w.Hwnd:X8}  {w}");
    if (Win32Windows.FindGame() is { } game)
    {
        string? exe = Win32Windows.ImagePath(game.Pid);
        Console.WriteLine($"game: {exe ?? "(executable path unknown)"}");
        Console.WriteLine($"log beside it: {(exe != null ? GameLog.BesideGame(exe) : null) ?? "-"}");
    }
    Console.WriteLine($"log found without the game: {GameLog.FindPath() ?? "-"}");
    return 0;
}

static int Capture(string[] args)
{
    // capture [title regex] [--out file.png]: one frame of the game (or the first window whose title matches).
    string? pattern = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : null;
    string outPath = Path.GetFullPath(Opt(args, "--out") ?? $"capture_{Dataset.NewId(DateTimeOffset.Now)}.png");
    var window = pattern == null
        ? Win32Windows.FindGame()
        : Win32Windows.List().FirstOrDefault(w => System.Text.RegularExpressions.Regex.IsMatch(w.Title, pattern));
    if (window is not { } win || win.Hwnd == 0)
    {
        Console.Error.WriteLine("window not found; see `tartarus-scan windows`");
        return 1;
    }
    Console.WriteLine($"capturing {win} bounds {Win32Windows.VisibleBounds(win.Hwnd)}");
    using var cap = new WindowCapture(win.Hwnd);
    Frame? frame = null;
    var sw = Stopwatch.StartNew();
    while (frame == null && sw.ElapsedMilliseconds < 3000)
    {
        Thread.Sleep(30);
        frame = cap.Grab();
    }
    if (frame == null)
    {
        Console.Error.WriteLine("no frame within 3 s (is the window minimized?)");
        return 1;
    }
    var grabSw = Stopwatch.StartNew();
    frame = cap.Grab()!;
    Console.WriteLine($"frame {frame.Width}x{frame.Height}, first after {sw.ElapsedMilliseconds - grabSw.ElapsedMilliseconds} ms, grab {grabSw.Elapsed.TotalMilliseconds:0.0} ms, frames so far {cap.FrameCount}");
    ImageIO.SavePng(frame, outPath);
    Console.WriteLine($"saved {outPath}");
    using var scanner = MakeScanner(args);
    var res = scanner.Scan(frame);
    Console.WriteLine($"{scanner.Name} {res.TotalMs:0} ms: " + SandboxText.Describe(res));
    if (res.Text != null) Console.WriteLine($"text ({res.Text.OcrMs:0} ms): {res.Text}");
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("""
        usage:
          tartarus-scan scan <file|dir> [--truth truth.json] [--out dir] [--repeat N] [--quiet]
          tartarus-scan export <dataset dir> [--out dir]
          tartarus-scan sandbox-template <dataset dir>
          tartarus-scan migrate <dataset dir>
          tartarus-scan review <dataset dir> [--all] [--port 8765] [--no-serve]
          tartarus-scan windows
          tartarus-scan capture [title regex] [--out file.png]
          tartarus-scan ocr <file|dir> [--repeat N] [--side 960]
        """);
    return 2;
}

static int Scan(string[] args)
{
    if (args.Length == 0) return Usage();
    string target = Path.GetFullPath(args[0]);
    string? truthPath = Opt(args, "--truth"), outDir = Opt(args, "--out");
    int repeat = int.TryParse(Opt(args, "--repeat"), out var r) ? Math.Max(1, r) : 1;
    bool quiet = args.Contains("--quiet");

    var files = Directory.Exists(target)
        ? Directory.EnumerateFiles(target).Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList()
        : [target];
    if (truthPath == null)
    {
        string dir = Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;
        truthPath = new[] { Path.Combine(dir, "truth.json"), Path.Combine(dir, "..", "truth.json") }.FirstOrDefault(File.Exists);
    }
    var truth = truthPath != null ? Dataset.LoadTruth(truthPath) : [];
    if (truthPath != null) Console.WriteLine($"labels: {truthPath} ({truth.Count})");

    var sw = Stopwatch.StartNew();
    using var scanner = MakeScanner(args);
    Console.WriteLine($"{scanner.Name}, up in {sw.ElapsedMilliseconds} ms");

    var total = new Tally();
    double msSum = 0;
    int scanned = 0, found = 0;
    foreach (var file in files)
    {
        var frame = ImageIO.Load(file);
        ScanResult res = null!;
        for (int i = 0; i < repeat; i++)
        {
            res = scanner.Scan(frame);
            msSum += res.TotalMs;
            scanned++;
            if (repeat > 1 && !quiet) Console.WriteLine($"  run {i + 1}: {res.TotalMs:0} ms ({Timings(res)})");
        }
        if (res.Ok) found++;
        string name = Path.GetFileName(file);
        Console.WriteLine($"{name} {frame.Width}x{frame.Height}: {(res.Ok ? "Sandbox" : "no Sandbox")} in {res.TotalMs:0} ms ({Timings(res)})");
        if (!quiet) Console.WriteLine("  " + SandboxText.Describe(res).Replace("\n", "\n  "));
        if (!quiet && res.Text != null) Console.WriteLine($"  text ({res.Text.OcrMs:0} ms): {res.Text}");
        foreach (var note in res.Notes ?? []) if (!quiet) Console.WriteLine("  note: " + note);
        if (truth.TryGetValue(name, out var labels))
        {
            var (tally, errs) = Scoring.Score(res, labels);
            total.Add(tally);
            foreach (var e in errs) Console.WriteLine("  ✗ " + e);
        }
        if (outDir != null)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".json"), res.RawJson);
        }
    }
    Console.WriteLine($"{files.Count} files, Sandbox found in {found}, {msSum / Math.Max(1, scanned):0} ms per scan");
    if (total.Fields.Count > 0) Console.WriteLine("OVERALL " + total);
    return 0;
}

static int Export(string[] args)
{
    if (args.Length == 0) return Usage();
    var ds = new Dataset(args[0]);
    string outDir = Path.GetFullPath(Opt(args, "--out") ?? Path.Combine(ds.Root, "train"));
    var sw = Stopwatch.StartNew();
    var s = TrainingExport.Export(ds, outDir, SandboxPresence.FindTemplate() is { } t ? SandboxPresence.Load(t) : null);
    foreach (var line in s.Skipped) Console.WriteLine("  skipped " + line);
    Console.WriteLine($"{s.Frames} frames -> {s.Cells} cells, {s.Arrows} arrow spots, {s.Slots} slots in {outDir} ({sw.ElapsedMilliseconds} ms)");
    return 0;
}

static string? Opt(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int Ocr(string[] args)
{
    // ocr <file|dir> [--repeat N] [--side 960] [--lang ru]: every line of text the OCR finds, with timings.
    if (args.Length == 0) return Usage();
    string target = Path.GetFullPath(args[0]);
    int repeat = int.TryParse(Opt(args, "--repeat"), out var r) ? Math.Max(1, r) : 1;
    var files = Directory.Exists(target)
        ? Directory.EnumerateFiles(target, "*.png").Order(StringComparer.Ordinal).ToList()
        : [target];
    var sw = Stopwatch.StartNew();
    using var ocr = new OcrEngine(threads: int.TryParse(Opt(args, "--threads"), out var th) ? th : 4, gameLanguage: Opt(args, "--lang"));
    if (int.TryParse(Opt(args, "--side"), out var side)) ocr.Detector.LimitSide = side;
    if (int.TryParse(Opt(args, "--minw"), out var minw)) ocr.Recognizer.MinWidth = minw;
    if (int.TryParse(Opt(args, "--batch"), out var batch)) ocr.Recognizer.BatchSize = batch;
    Console.WriteLine($"models from {ocr.ModelDir}, recognizer {ocr.Pack.Id}, rec height {ocr.Recognizer.Height}, up in {sw.ElapsedMilliseconds} ms");
    foreach (var file in files)
    {
        var frame = ImageIO.Load(file);
        List<TextLine> lines = [];
        for (int i = 0; i < repeat; i++)
        {
            sw.Restart();
            lines = ocr.Read(frame);
            var t = ocr.LastTiming;
            Console.WriteLine($"{Path.GetFileName(file)} {frame.Width}x{frame.Height}: {sw.Elapsed.TotalMilliseconds:0} ms (detect {t.DetectMs:0}, recognize {t.RecognizeMs:0}, {t.Regions} regions, {lines.Count} lines)");
        }
        foreach (var l in lines) Console.WriteLine("  " + l);
    }
    return 0;
}

static int OcrGet(string language)
{
    // ocr-get <game language code>: downloads that language's OCR recognizer, as the app offers to.
    var pack = OcrPacks.ForGame(language);
    if (OcrPacks.Locate(pack, OcrEngine.FindModelDir()) is { } dir)
    {
        Console.WriteLine($"{pack.Id}: already in {dir}");
        return 0;
    }
    OcrDownload.RunAsync(pack, new TenthsProgress(), CancellationToken.None).GetAwaiter().GetResult();
    Console.WriteLine($"{pack.Id}: downloaded to {OcrPacks.DownloadDir}");
    return 0;
}

// The screen layout, OCR by region (--lang: the game's language code), and the trained cell and slot networks.
static ISandboxScanner MakeScanner(string[] args)
{
    var cells = new CnnCellReader(panels: new SlotReader());
    var presence = SandboxPresence.FindTemplate() is { } t ? SandboxPresence.Load(t) : null;
    return new LayoutScanner(new OcrEngine(gameLanguage: Opt(args, "--lang")), cells, presence);
}

static string Timings(ScanResult r) =>
    r.Timings.Count == 0 ? $"{r.Ms:0} ms" : string.Join(", ", r.Timings.Select(t => $"{t.Key} {t.Value:0}"));

static int SandboxTemplate(string[] args)
{
    // Sandbox-template <dataset>: the presence template from the dataset's Sandbox frames. Seeds: frames whose scan
    // read the day counter (certainly Sandboxes, though a pick screen can keep the counter); then again from the
    // frames that matched the first template well, which drops those.
    if (args.Length == 0) return Usage();
    var ds = new Dataset(args[0]);
    var frames = new List<(string Name, float[] Features, bool Seed)>();
    foreach (var path in Directory.EnumerateFiles(ds.FramesDir, "*.png").Order(StringComparer.Ordinal))
    {
        string id = Path.GetFileNameWithoutExtension(path), scanPath = Path.Combine(ds.Root, "scans", id + ".json");
        var frame = ImageIO.Load(path);
        var layout = new ScreenLayout(frame.Width, frame.Height);
        var scan = File.Exists(scanPath) ? ScanJson.Parse(File.ReadAllText(scanPath), "file") : null;
        // Frames with the window's frame around the client area (saved before cropping) don't line up.
        if (scan?.Loc is { } loc && (Math.Abs(loc.Tx - layout.Sandbox.Tx) > 4 || Math.Abs(loc.Ty - layout.Sandbox.Ty) > 4)) continue;
        frames.Add((Path.GetFileName(path), SandboxPresence.Features(frame, layout), scan?.Days != null));
    }
    var first = SandboxPresence.Build(frames.Where(f => f.Seed).Select(f => f.Features));
    var template = SandboxPresence.Build(frames.Where(f => f.Seed && first.Score(f.Features) >= 0.2f).Select(f => f.Features));
    var scores = frames.Select(f => (f.Name, f.Seed, Score: template.Score(f.Features))).ToList();
    string outPath = Path.Combine(Tartarus.AppFiles.Root ?? Directory.GetCurrentDirectory(), "models", "sandbox", SandboxPresence.FileName);
    template.Save(outPath);
    var boards = scores.Where(s => s.Score >= SandboxPresence.DefaultThreshold).ToList();
    var others = scores.Where(s => s.Score < SandboxPresence.DefaultThreshold).ToList();
    Console.WriteLine($"{frames.Count} frames: {boards.Count} Sandbox frames (lowest {boards.Select(s => s.Score).DefaultIfEmpty().Min():0.000}), {others.Count} other screens (highest {others.Select(s => s.Score).DefaultIfEmpty().Max():0.000})");
    foreach (var s in scores.Where(s => s.Seed && s.Score < SandboxPresence.DefaultThreshold)) Console.WriteLine($"  day counter read but not a Sandbox: {s.Name} {s.Score:0.000}");
    foreach (var s in scores.Where(s => s.Score is > 0.08f and < 0.25f)) Console.WriteLine($"  near the threshold: {s.Name} {s.Score:0.000}");
    Console.WriteLine($"saved {outPath}");
    return 0;

}

static int Migrate(string[] args)
{
    if (args.Length == 0) return Usage();
    var ds = new Dataset(args[0]);
    Console.WriteLine($"truth.json: {ds.RewriteTruth()} entries rewritten in the game's terms; the old file is truth.json.bak");
    return 0;
}

/// <summary>Download progress printed every tenth, as it comes (Progress&lt;T&gt; would post it out of order).</summary>
sealed class TenthsProgress : IProgress<(long Done, long Total)>
{
    private long _shown = -1;

    public void Report((long Done, long Total) p)
    {
        long tenth = p.Done * 10 / Math.Max(1, p.Total);
        if (tenth == _shown) return;
        _shown = tenth;
        Console.WriteLine($"  {tenth * 10}% of {p.Total / 1048576.0:0.0} MB");
    }
}
