using System.Net;
using System.Text;
using System.Text.Json;
using Tartarus.Vision;

/// <summary>
/// tartarus-scan review: every Sandbox frame's cells through the cell networks, against truth.json. Each
/// disagreement is shown one at a time on a local page (http://localhost:PORT): the labels are right, the network
/// is right (its reading goes into truth.json), neither (set the cell by hand), or the cell is covered by the
/// game's UI. Every decision marks the cell as checked by hand (meta EditedCells), which training trusts and the
/// next review skips. A list of all disagreements, without the buttons, is written to review/index.html.
/// --flips: only what may move a hint, on plain Sandbox screens: a cell read otherwise for one frame (an Inmate in it
/// or around it, not Unopened, Empty and covered alone) and cells the network read unsurely.
/// --cells &lt;file.json&gt;: the cells listed ([{frame, cell, old, new}], two networks' readings that differ), on any
/// screen: "the network is right" puts in the reading of the two that isn't the label's.
/// </summary>
static class Review
{
    sealed record Item(int Id, string File, string FrameId, int Cell, string Label, string Net,
        CellLabel? LabelCell, CellLabel? NetCell, int LabelWarden, int NetWarden, bool Plain, string Image, string Note = "")
    {
        public bool IsWarden => Cell == 0;
    }

    public static int Run(string[] args, Func<string[], string, string?> opt)
    {
        var ds = new Dataset(args[0]);
        bool flips = args.Contains("--flips");
        bool all = args.Contains("--all"), serve = !args.Contains("--no-serve"), suspect = args.Contains("--suspect") || flips, red = args.Contains("--red"), mouth = args.Contains("--mouth") || suspect, covered = args.Contains("--covered"), marks = args.Contains("--marks"), found = args.Contains("--found"), nests = args.Contains("--nests");
        string? since = opt(args, "--since");
        var listed = opt(args, "--cells") is { } cellsFile ? Listed(cellsFile) : null;
        int port = int.TryParse(opt(args, "--port"), out var p) ? p : 8765;
        string outDir = Path.Combine(ds.Root, "review"), imgDir = Path.Combine(outDir, "img");
        Directory.CreateDirectory(imgDir);
        foreach (var f in Directory.EnumerateFiles(imgDir, "*.png")) File.Delete(f);

        Console.WriteLine("reading the frames through the networks (the page starts after that, about a minute)");
        var (items, frames, cells, wardens) = Collect(ds, imgDir, all, suspect, red, mouth, covered, marks, found, nests, since, listed);
        if (flips)
        {
            static bool Inmate(string cell) => !(cell.StartsWith("Unopened") || cell.StartsWith("Empty") || cell.StartsWith("Covered"));
            items = items.Where(i => i.Plain && !i.IsWarden && (i.Note.Contains("сеть не уверена")
                    || i.Note.StartsWith(FlipNote) && (Inmate(i.Label) || Inmate(i.Note[FlipNote.Length..].Trim()))))
                .Select((i, k) => i with { Id = k }).ToList();
        }
        Console.WriteLine();
        int wrongCells = items.Count(i => !i.IsWarden), wrongWardens = items.Count(i => i.IsWarden);
        int mouthNotes = items.Count(i => i.Note.Contains("по правилам", StringComparison.Ordinal));
        if (mouth) Console.WriteLine($"the small mouth against the rules: {mouthNotes} cells");
        File.WriteAllText(Path.Combine(outDir, "index.html"), ListPage(items, frames, cells, wardens));
        Console.WriteLine($"{frames} frames: cells {cells - wrongCells}/{cells} agree, warden {wardens - wrongWardens}/{wardens}" + (all ? "" : " (cells checked by hand left out)"));
        if (!serve || items.Count == 0) return 0;

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        Console.WriteLine($"review page: http://localhost:{port}/  (Ctrl+C to stop)");
        var decided = new HashSet<int>();
        while (listener.IsListening)
        {
            var ctx = listener.GetContext();
            try { Handle(ctx, ds, items, decided, imgDir); }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                Write(ctx, "text/plain; charset=utf-8", ex.Message);
            }
        }
        return 0;
    }

    // suspect: also cells of frames the networks labeled themselves (no disagreement to show there) that look wrong:
    // read as Ultimate (a Consume pick's red highlight passes for it), read unsurely, or flipping for one frame.
    // red: only the Consume pick's red frames that carry-red labeled, and in them the cells it couldn't (an Inmate under
    // the jaws or the red, the eater, a cell Avenger's Casket opened), with what the plain frames around read there.
    // mouth: cells where the networks see the small mouth (the game's "has a target" mark) though the rules, from the
    // cells and instruments the networks read, find no target: a Code or rarity read wrong, this cell's or a neighbour's.
    // nests (with --since <frame id>): Rat Nest, Tusk and Prey on the frames from then on: the cells where the cell
    // network sees one, and the cells that could carry one it doesn't see (Unopened ones while Squirming Iron Box is
    // held, Mammoths while Iron Tongs of Lust, any Inmate while the Blood-Stained Trap); a cell shown again only when its
    // reading changed since its last showing.
    static (List<Item> Items, int Frames, int Cells, int Wardens) Collect(Dataset ds, string imgDir, bool all, bool suspect, bool red = false, bool mouth = false, bool covered = false, bool marks = false, bool found = false, bool nests = false, string? since = null,
        Dictionary<string, List<(int Cell, string Old, string New)>>? listed = null)
    {
        var lastShown = new Dictionary<int, string>();
        var engine = new Tartarus.Rules.Engine();
        var plainFrames = red ? PlainFrames(ds) : [];
        var byNet = new List<(string Id, string File, string Path, DateTime At, EditableSandbox Want, EditableSandbox Got, bool Plain, SampleMeta? Meta)>();
        var presence = SandboxPresence.FindTemplate() is { } t ? SandboxPresence.Load(t) : null;
        using var cnn = new CnnCellReader();
        var items = new List<Item>();
        int frames = 0, cells = 0, wardens = 0;
        foreach (var (file, labels) in Dataset.LoadTruth(ds.TruthPath).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string id = Path.GetFileNameWithoutExtension(file), framePath = Path.Combine(ds.FramesDir, file);
            if (!File.Exists(framePath) || new FileInfo(framePath).Length == 0) continue;
            if (since != null && string.CompareOrdinal(id, since) < 0) continue;
            // --red alone: only the red frames carry-red labeled; with --mouth the other frames still give their mouths.
            bool redFrame = ds.MetaOf(id)?.CarriedCells != null;
            if (red && !redFrame && !mouth) continue;
            if (listed != null && !listed.ContainsKey(id)) continue;
            var frame = ImageIO.Load(framePath);
            var layout = new ScreenLayout(frame.Width, frame.Height);
            // --cells: the listed cells alone, on any screen (a dim or fading one is where networks part).
            if (listed != null)
            {
                var have = EditableSandbox.FromLabels(labels);
                var known = ds.MetaOf(id);
                frames++;
                foreach (var (n, older, newer) in listed[id])
                {
                    if (!all && known?.EditedCells.Contains(n) == true) continue;
                    cells++;
                    var other = have[n].Clone();
                    string alt = Reading(have[n]) == older ? newer : older;
                    SetReading(other, alt);
                    var (lx, ly, lk) = layout.Cell(n);
                    string limg = $"{id}_c{n:00}.png";
                    ImageIO.SavePng(CellCrops.CropBox(frame, lx - 90 * lk, ly - 140 * lk, lx + 110 * lk, ly + 40 * lk, 400, 360), Path.Combine(imgDir, limg));
                    items.Add(new Item(items.Count, file, id, n, SandboxText.CellShort(have[n]), SandboxText.CellShort(other), have[n].Clone(), other, 0, 0, false, limg,
                        $"сети расходятся: старая — {older}, новая — {newer}; «права сеть» запишет {alt}"));
                }
                continue;
            }
            if (presence != null && presence.Score(frame, layout) < presence.Threshold) continue;
            string scanPath = Path.Combine(ds.Root, "scans", id + ".json");
            var scan = File.Exists(scanPath) ? ScanJson.Parse(File.ReadAllText(scanPath), "file") : null;
            // Frames saved with the window's frame around them don't line up with the layout.
            if (scan?.Loc is { } loc && (Math.Abs(loc.Tx - layout.Sandbox.Tx) > 4 || Math.Abs(loc.Ty - layout.Sandbox.Ty) > 4)) continue;
            bool plain = scan?.Days != null;
            var meta = ds.MetaOf(id);
            var want = EditableSandbox.FromLabels(labels);
            if (labels.VisibleRarity == null && labels.ScratchMarks == null && scan?.Cells != null)
                foreach (var c in scan.Cells)
                    if (c.N > 0 && want.Cells[c.N].Kind == CellKind.Unopened && c.VisibleRarity is { } s)
                        (want.Cells[c.N].VisibleRarity, want.Cells[c.N].ScratchMarks) = (s.Rarity, s.ScratchMarks);
            var read = cnn.Read(frame, layout);
            var got = EditableSandbox.FromScan(read);
            bool labeledByNet = meta?.Scanner?.Contains("cnn", StringComparison.Ordinal) == true;
            if (suspect && labeledByNet) byNet.Add((id, file, framePath, DateTime.ParseExact(id, "yyyyMMdd_HHmmss_fff", null), want, got, plain, meta));
            frames++;
            if (frames % 20 == 0) Console.Write($"\r{frames} frames read...");
            for (int n = 2; n <= 16; n++)
            {
                cells++;
                string a = SandboxText.CellShort(want[n]), b = SandboxText.CellShort(got[n]);
                string note = "";
                // covered: every cell labeled as hidden by the game's UI, checked again (one may show its Inmate after all).
                if (covered)
                {
                    // The ones set by hand (the scanner's own "covered" are mostly tooltips); --all: every one.
                    if (!want[n].Covered || (!all && meta?.EditedCells.Contains(n) != true)) continue;
                    var (cx, cy, ck) = layout.Cell(n);
                    string cimg = $"{id}_c{n:00}.png";
                    ImageIO.SavePng(CellCrops.CropBox(frame, cx - 90 * ck, cy - 140 * ck, cx + 110 * ck, cy + 40 * ck, 400, 360), Path.Combine(imgDir, cimg));
                    items.Add(new Item(items.Count, file, id, n, SandboxText.CellShort(want[n]), b, want[n].Clone(), got[n].Clone(), 0, 0, plain, cimg,
                        "отмечено как закрытое интерфейсом: если зверя видно, выбери его (сеть или вручную); иначе оставь"));
                    continue;
                }
                // found: the marks the game's icons found (meta MarkCells), checked by hand.
                if (found)
                {
                    if (meta?.MarkCells?.Contains(n) != true || (!all && meta.EditedCells.Contains(n))) continue;
                    var (fx, fy, fk) = layout.Cell(n);
                    string fimg = $"{id}_c{n:00}.png";
                    ImageIO.SavePng(CellCrops.CropBox(frame, fx - 90 * fk, fy - 140 * fk, fx + 110 * fk, fy + 40 * fk, 400, 360), Path.Combine(imgDir, fimg));
                    items.Add(new Item(items.Count, file, id, n, SandboxText.CellShort(want[n]), b, want[n].Clone(), got[n].Clone(), 0, 0, plain, fimg,
                        "метка найдена по значку игры: верно — «права разметка» (1); метки нет или не та — «ни то, ни другое» (3) и поправь флаги"));
                    continue;
                }
                if (nests)
                {
                    if (want[n].Covered || got[n].Covered || (!all && meta?.EditedCells.Contains(n) == true)) continue;
                    var held = want.Instruments;
                    bool seen = got[n].Nest || got[n].Tusk || got[n].Prey;
                    // A Tusk mark is the cell's: it stays under whoever comes in after the Mammoth (2026-10-30: on a
                    // Wolf and a Lion, never read).
                    bool could = held.Contains("ironbox") && got[n].Kind == CellKind.Unopened
                        || (held.Contains("tongs") || held.Contains("trap")) && got[n].Kind == CellKind.Inmate;
                    if (!seen && !could) continue;
                    string sig = SandboxText.CellShort(got[n]);
                    if (lastShown.TryGetValue(n, out var before) && before == sig) continue;
                    lastShown[n] = sig;
                    var (nx, ny, nk) = layout.Cell(n);
                    string nimg = $"{id}_c{n:00}.png";
                    ImageIO.SavePng(CellCrops.CropBox(frame, nx - 90 * nk, ny - 140 * nk, nx + 110 * nk, ny + 40 * nk, 400, 360), Path.Combine(imgDir, nimg));
                    items.Add(new Item(items.Count, file, id, n, SandboxText.CellShort(want[n]), b, want[n].Clone(), got[n].Clone(), 0, 0, plain, nimg,
                        seen ? "сеть видит отметку: верно — «права сеть» (2); нет её — «ни то, ни другое» (3) и сними флаг"
                             : "может быть Rat Nest / Tusk / Prey: нет отметки — «права разметка» (1); есть — «ни то, ни другое» (3) и поставь флаг"));
                    continue;
                }
                // marks: the Inmates that may carry a mark the networks haven't learned yet — each Inmate while Iron
                // Tongs of Lust is held (Tusk: the cell's, whoever is in it) or the Blood-Stained Trap is (Prey).
                if (marks)
                {
                    bool tongs = want.Instruments.Contains("tongs"), trap = want.Instruments.Contains("trap");
                    if (want[n].Kind != CellKind.Inmate || want[n].Covered || !(tongs || trap)) continue;
                    if (!all && meta?.EditedCells.Contains(n) == true) continue;
                    var (mx, my, mk) = layout.Cell(n);
                    string mimg = $"{id}_c{n:00}.png";
                    ImageIO.SavePng(CellCrops.CropBox(frame, mx - 90 * mk, my - 140 * mk, mx + 110 * mk, my + 40 * mk, 400, 360), Path.Combine(imgDir, mimg));
                    string what = tongs ? (trap ? "Tusk (бивень) или Prey" : "Tusk (бивень)") : "Prey";
                    items.Add(new Item(items.Count, file, id, n, SandboxText.CellShort(want[n]), b, want[n].Clone(), got[n].Clone(), 0, 0, plain, mimg,
                        $"есть ли метка {what}? Нет — «права разметка» (1); есть — «ни то, ни другое» (3) и флаг {what.Split(' ')[0]}"));
                    continue;
                }
                if (red && !redFrame) continue;
                // --mouth alone: only its suspects, not every disagreement with the labels.
                if (mouth && !red && !suspect) continue;
                if (red)
                {
                    if (meta!.CarriedCells!.Contains(n) || (!all && meta.EditedCells.Contains(n))) continue;
                    if (want[n].Kind != CellKind.Inmate && got[n].Kind != CellKind.Inmate && !got[n].Covered && !want[n].Covered) continue;
                    note = "красный экран" + Around(plainFrames, id, want.Warden, n);
                }
                else if (want[n].Covered || (!all && meta?.EditedCells.Contains(n) == true)) continue;
                if (a == b && suspect && labeledByNet)
                {
                    if (got[n].Kind == CellKind.Inmate && got[n].Rarity == Rarity.Ultimate) note = "подозрение: Ultimate";
                    else if (read.Cells?[n - 1].Conf < 0.9) note = $"подозрение: сеть не уверена ({read.Cells[n - 1].Conf:0.00})";
                }
                if (a == b && note.Length == 0) continue;
                if (red && a == b) a += " (сеть)";
                var (x, y, k) = layout.Cell(n);
                string img = $"{id}_c{n:00}.png";
                ImageIO.SavePng(CellCrops.CropBox(frame, x - 90 * k, y - 140 * k, x + 110 * k, y + 40 * k, 400, 360), Path.Combine(imgDir, img));
                items.Add(new Item(items.Count, file, id, n, a, b, want[n].Clone(), got[n].Clone(), 0, 0, plain, img, note));
            }
            if (mouth)
            {
                var board = got.Clone();
                board.Instruments = [.. want.Instruments];
                // The run's theme too: in Cage Fight lower rarities are targets (2026-10-28: 306 of 341 suspects were that).
                board.Theme = want.Theme;
                board.Warden = want.Warden > 0 ? want.Warden : got.Warden;
                var rules = engine.FromObservation(GameObservation.From(board), 1);
                var have = items.Select(i => (i.FrameId, i.Cell)).ToHashSet();
                for (int n = 2; n <= 16; n++)
                {
                    var c = got[n];
                    if (c.Kind != CellKind.Inmate || c.Covered || have.Contains((id, n)) || (!all && meta?.EditedCells.Contains(n) == true)) continue;
                    // Only a mouth the rules can't explain: a mouth the networks miss is common (red on an Ultimate's
                    // red glow) and says nothing of the Codes and rarities.
                    bool target = Tartarus.Rules.Engine.HasTarget(rules, n - 1);
                    if (!c.Consumable || target) continue;
                    var (x, y, k) = layout.Cell(n);
                    string img = $"{id}_c{n:00}.png";
                    ImageIO.SavePng(CellCrops.CropBox(frame, x - 90 * k, y - 140 * k, x + 110 * k, y + 40 * k, 400, 360), Path.Combine(imgDir, img));
                    string note = c.Consumable
                        ? "подозрение: на картинке пасть (есть цель), а по правилам у него целей нет — проверь Code и Rarity, его и соседей"
                        : "подозрение: пасти на картинке нет, а по правилам цель есть — проверь Code и Rarity, его и возможной цели";
                    items.Add(new Item(items.Count, file, id, n, SandboxText.CellShort(want[n]), SandboxText.CellShort(c), want[n].Clone(), c.Clone(), 0, 0, plain, img, note));
                }
            }
            if (want.Warden > 0 && !red && !covered && !marks && !found && !nests && !(mouth && !suspect))
            {
                wardens++;
                if (got.Warden != want.Warden && (all || meta?.SandboxEdited != true))
                {
                    string img = $"{id}_warden.png";
                    var box = layout.ToPixels(SandboxPresence.Region);
                    ImageIO.SavePng(CellCrops.CropBox(frame, box.X0, box.Y0, box.X1, box.Y1, 920, 520), Path.Combine(imgDir, img));
                    items.Add(new Item(items.Count, file, id, 0, $"Warden {want.Warden}", $"Warden {(got.Warden > 0 ? got.Warden : "?")}", null, null, want.Warden, got.Warden, plain, img));
                }
            }
        }
        if (suspect) AddFlips(byNet, items, imgDir, all);
        return (items, frames, cells, wardens);
    }

    /// <summary>The frames whose scan read the day counter, in time order, with their labels.</summary>
    static List<(string Id, DateTime At, EditableSandbox Labels)> PlainFrames(Dataset ds)
    {
        var list = new List<(string, DateTime, EditableSandbox)>();
        foreach (var (file, labels) in Dataset.LoadTruth(ds.TruthPath).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string id = Path.GetFileNameWithoutExtension(file), scanPath = Path.Combine(ds.Root, "scans", id + ".json");
            if (File.Exists(scanPath) && ScanJson.Parse(File.ReadAllText(scanPath), "file").Days != null)
                list.Add((id, DateTime.ParseExact(id, "yyyyMMdd_HHmmss_fff", null), EditableSandbox.FromLabels(labels)));
        }
        return list;
    }

    /// <summary>"; до (Warden 5): Wolf Rare; после: Unopened": cell n in the plain frames just around (within 30 s).</summary>
    static string Around(List<(string Id, DateTime At, EditableSandbox Labels)> plain, string id, int warden, int n)
    {
        var at = DateTime.ParseExact(id, "yyyyMMdd_HHmmss_fff", null);
        var before = plain.LastOrDefault(p => p.At < at && (at - p.At).TotalSeconds <= 30);
        var after = plain.FirstOrDefault(p => p.At > at && (p.At - at).TotalSeconds <= 30);
        string Side(string name, (string Id, DateTime At, EditableSandbox Labels) p) => p.Labels == null ? ""
            : $"; {name}{(p.Labels.Warden != warden ? $" (Warden {p.Labels.Warden})" : "")}: {SandboxText.CellShort(p.Labels[n])}";
        return Side("до", before) + Side("после", after) + (n == warden ? "; это клетка Warden (едок)" : "");
    }

    // A cell that reads the same in the frames just before and after (within 20 s) but not in this one: the odd one out.
    const string FlipNote = "подозрение: в кадрах до и после — ";

    /// <summary>--cells' list by frame id.</summary>
    static Dictionary<string, List<(int Cell, string Old, string New)>> Listed(string path)
    {
        var map = new Dictionary<string, List<(int, string, string)>>();
        foreach (var e in JsonDocument.Parse(File.ReadAllText(path)).RootElement.EnumerateArray())
        {
            string frame = e.GetProperty("frame").GetString()!;
            if (!map.TryGetValue(frame, out var list)) map[frame] = list = [];
            list.Add((e.GetProperty("cell").GetInt32(), e.GetProperty("old").GetString()!, e.GetProperty("new").GetString()!));
        }
        return map;
    }

    /// <summary>A cell as a network's reading is written: "Lion Ultimate", "Unopened ?Rare", "Empty", "Covered".</summary>
    static string Reading(CellLabel c) => c.Covered ? "Covered" : c.Kind switch
    {
        CellKind.Inmate => $"{Codes.Name(c.Code)} {c.Rarity}",
        CellKind.Unopened => "Unopened" + (c.VisibleRarity is { } r ? $" ?{r}" : ""),
        _ => "Empty",
    };

    static void SetReading(CellLabel c, string reading)
    {
        c.Covered = reading == "Covered";
        if (c.Covered) return;
        if (reading == "Empty") c.Kind = CellKind.Empty;
        else if (reading.StartsWith("Unopened", StringComparison.Ordinal))
        {
            c.Kind = CellKind.Unopened;
            c.VisibleRarity = reading.Contains('?') ? Enum.Parse<Rarity>(reading[(reading.IndexOf('?') + 1)..]) : null;
        }
        else
        {
            var parts = reading.Split(' ');
            (c.Kind, c.Code, c.Rarity) = (CellKind.Inmate, Codes.Parse(parts[0]), Enum.Parse<Rarity>(parts[1]));
        }
    }

    static void AddFlips(List<(string Id, string File, string Path, DateTime At, EditableSandbox Want, EditableSandbox Got, bool Plain, SampleMeta? Meta)> frames, List<Item> items, string imgDir, bool all)
    {
        var have = items.Select(i => (i.FrameId, i.Cell)).ToHashSet();
        for (int f = 1; f + 1 < frames.Count; f++)
        {
            var (a, b, c) = (frames[f - 1], frames[f], frames[f + 1]);
            if ((c.At - a.At).TotalSeconds > 20) continue;
            Frame? image = null;
            for (int n = 2; n <= 16; n++)
            {
                string x = SandboxText.CellShort(a.Want[n]), y = SandboxText.CellShort(b.Want[n]), z = SandboxText.CellShort(c.Want[n]);
                if (x != z || y == x || have.Contains((b.Id, n)) || (!all && b.Meta?.EditedCells.Contains(n) == true)) continue;
                image ??= ImageIO.Load(b.Path);
                var layout = new ScreenLayout(image.Width, image.Height);
                var (cx, cy, k) = layout.Cell(n);
                string img = $"{b.Id}_c{n:00}.png";
                ImageIO.SavePng(CellCrops.CropBox(image, cx - 90 * k, cy - 140 * k, cx + 110 * k, cy + 40 * k, 400, 360), System.IO.Path.Combine(imgDir, img));
                items.Add(new Item(items.Count, b.File, b.Id, n, y, SandboxText.CellShort(b.Got[n]), b.Want[n].Clone(), b.Got[n].Clone(), 0, 0, b.Plain, img, $"{FlipNote}{x}"));
            }
        }
    }

    static void Handle(HttpListenerContext ctx, Dataset ds, List<Item> items, HashSet<int> decided, string imgDir)
    {
        string path = ctx.Request.Url!.AbsolutePath;
        if (path == "/")
        {
            Write(ctx, "text/html; charset=utf-8", ReviewPage(items.Where(i => !decided.Contains(i.Id)).ToList()));
            return;
        }
        if (path.StartsWith("/img/", StringComparison.Ordinal))
        {
            string file = Path.Combine(imgDir, Path.GetFileName(path));
            if (!File.Exists(file))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            ctx.Response.ContentType = "image/png";
            ctx.Response.OutputStream.Write(File.ReadAllBytes(file));
            ctx.Response.Close();
            return;
        }
        if (path == "/decide" && ctx.Request.HttpMethod == "POST")
        {
            int id = int.Parse(ctx.Request.QueryString["item"]!);
            string choice = ctx.Request.QueryString["choice"]!;
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
            var item = items[id];
            string what = Apply(ds, item, choice, body);
            decided.Add(id);
            Console.WriteLine($"{item.File} {(item.IsWarden ? "warden" : $"cell {item.Cell}")}: {choice} → {what}");
            Write(ctx, "application/json", "{\"ok\":true}");
            return;
        }
        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
    }

    // label: truth.json stays; net: the network's reading goes in; set: what was picked by hand (JSON body);
    // covered: the cell is hidden by the game's UI. Returns what the cell (or warden) is now.
    static string Apply(Dataset ds, Item item, string choice, string body)
    {
        var labels = ds.LabelsOf(item.File) ?? throw new InvalidOperationException($"{item.File} has no labels");
        var sandbox = EditableSandbox.FromLabels(labels);
        if (item.IsWarden)
        {
            if (choice == "net") sandbox.Warden = item.NetWarden;
            else if (choice == "set") sandbox.Warden = JsonDocument.Parse(body).RootElement.GetProperty("warden").GetInt32();
            ds.SetLabels(item.File, sandbox.ToLabels());
            ds.MarkChecked(item.FrameId, sandbox: true);
            return $"Warden {sandbox.Warden}";
        }
        var cell = sandbox[item.Cell];
        if (choice == "net" && item.NetCell is { } net) Copy(net, cell);
        else if (choice == "set") Copy(Parse(JsonDocument.Parse(body).RootElement, item.Cell), cell);
        else if (choice == "covered") cell.Covered = true;
        ds.SetLabels(item.File, sandbox.ToLabels());
        ds.MarkChecked(item.FrameId, cell: item.Cell);
        return cell.Covered ? "covered" : SandboxText.CellShort(cell);
    }

    static void Copy(CellLabel from, CellLabel to)
    {
        to.Kind = from.Kind;
        to.Code = from.Code;
        to.Rarity = from.Rarity;
        to.Consumable = from.Consumable;
        to.Nest = from.Nest;
        to.Incident = from.Incident;
        to.VisibleRarity = from.VisibleRarity;
        to.ScratchMarks = from.ScratchMarks;
        to.Tusk = from.Tusk;
        to.Prey = from.Prey;
        to.Covered = from.Covered;
    }

    // The page's editor: {"kind":"Inmate","code":"Wolf","rarity":"Rare","consumable":true,"incident":false,
    // "visible":null,"scratchMarks":false,"nest":false,"tusk":false,"prey":false}.
    static CellLabel Parse(JsonElement e, int n)
    {
        string? Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Flag(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        var kind = Enum.Parse<CellKind>(Str("kind") ?? "Empty");
        var c = new CellLabel { N = n, Kind = kind, Incident = kind != CellKind.Empty && Flag("incident") };
        if (kind == CellKind.Inmate)
        {
            c.Code = Codes.Parse(Str("code"));
            c.Rarity = Rarities.Parse(Str("rarity")) ?? Rarity.Normal;
            c.Consumable = Flag("consumable");
            c.Tusk = Flag("tusk");
            c.Prey = Flag("prey");
        }
        else if (kind == CellKind.Unopened)
        {
            c.VisibleRarity = Rarities.Parse(Str("visible"));
            c.ScratchMarks = Flag("scratchMarks");
            c.Nest = Flag("nest");
        }
        return c;
    }

    static object? CellJson(CellLabel? c) => c == null ? null : new
    {
        kind = c.Kind.ToString(),
        code = c.Code?.ToString(),
        rarity = c.Rarity.ToString(),
        consumable = c.Consumable,
        incident = c.Incident,
        visible = c.VisibleRarity?.ToString(),
        scratchMarks = c.ScratchMarks,
        nest = c.Nest,
        tusk = c.Tusk,
        prey = c.Prey,
    };

    // One card at a time in the middle of the screen; 1 labels, 2 network, 3 neither (set it by hand), 4 covered.
    static string ReviewPage(List<Item> items)
    {
        var data = JsonSerializer.Serialize(items.Select(i => new
        {
            id = i.Id,
            image = "/img/" + i.Image,
            frame = i.FrameId,
            cell = i.Cell,
            special = !i.Plain,
            label = i.Label,
            net = i.Net,
            labelCell = CellJson(i.LabelCell),
            netCell = CellJson(i.NetCell),
            labelWarden = i.LabelWarden,
            netWarden = i.NetWarden,
            note = i.Note,
        }));
        var codes = JsonSerializer.Serialize(Codes.All.Select(c => c.ToString()));
        var rarities = JsonSerializer.Serialize(Enumerable.Range(0, (int)Rarity.Ultimate + 1).Select(r => ((Rarity)r).ToString()));
        return """
            <!doctype html><html lang="ru"><meta charset="utf-8"><title>Проверка клеток</title>
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <style>
            :root { color-scheme: dark; --bg:#1b1c21; --card:#25262d; --line:#383a44; --text:#ececf0; --dim:#9aa0ac; --accent:#e8b84a; --lab:#4caf6a; --net:#3d8bd9; }
            * { box-sizing: border-box; }
            body { background:var(--bg); color:var(--text); font:16px "Segoe UI", sans-serif; margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center; }
            main { width:min(960px, 100% - 32px); display:flex; flex-direction:column; align-items:center; gap:14px; padding:20px 0; }
            .counter { width:100%; display:flex; justify-content:space-between; align-items:baseline; font-size:15px; color:var(--dim); }
            .counter b { color:var(--text); font-size:20px; }
            .bar { width:100%; height:6px; background:var(--line); border-radius:3px; overflow:hidden; }
            .bar > div { height:100%; background:var(--accent); transition:width .2s; }
            .where { color:var(--dim); font-size:14px; align-self:flex-start; } .special { color:var(--accent); }
            img { max-width:100%; height:auto; image-rendering:auto; border:1px solid var(--line); border-radius:6px; }
            .reads { width:100%; display:grid; grid-template-columns:auto 1fr; gap:6px 14px; font-size:20px; }
            .reads .k { color:var(--dim); font-size:16px; align-self:center; }
            .btns { width:100%; display:grid; grid-template-columns:repeat(4, 1fr); gap:10px; }
            button { background:#30323a; color:var(--text); border:1px solid #4a4d58; border-radius:8px; padding:12px 10px; font:inherit; cursor:pointer; }
            button:hover { background:#3a3d47; } button kbd { color:var(--dim); margin-right:6px; font:inherit; }
            button.lab { border-color:var(--lab); } button.net { border-color:var(--net); } button:disabled { opacity:.35; cursor:default; }
            .nav { width:100%; display:flex; justify-content:space-between; color:var(--dim); font-size:14px; }
            .nav button { padding:6px 12px; font-size:14px; }
            .editor { width:100%; background:var(--card); border:1px solid var(--line); border-radius:8px; padding:14px; display:none; flex-direction:column; gap:10px; }
            .editor.open { display:flex; }
            .editor .row { display:flex; flex-wrap:wrap; gap:6px; align-items:center; }
            .editor .row .k { color:var(--dim); width:120px; font-size:14px; }
            .editor .opt { padding:6px 12px; font-size:15px; } .editor .opt.on { background:var(--accent); color:#111; border-color:var(--accent); }
            .done { font-size:22px; text-align:center; color:var(--dim); }
            </style>
            <main id="app"></main>
            <script>
            """ + $"const items = {data}; const CODES = {codes}; const RARITIES = {rarities};" + """

            const total = items.length;
            const state = items.map(() => null);   // what was decided for each card
            const skipped = new Set();              // passed over with space or →; shown again at the end
            const pending = i => state[i] === null && !skipped.has(i);
            let at = 0;                             // the card shown; -1 when only skipped ones are left
            let editing = null;
            const app = document.getElementById('app');
            const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;' }[c]));

            function render() {
              const left = state.filter(s => s === null).length;
              const passed = [...skipped].filter(i => state[i] === null).length;
              if (left === 0) { app.innerHTML = `<div class="done">Готово: проверено ${total} из ${total}. Можно закрыть страницу и переобучить сети.</div>`; return; }
              if (at < 0) {
                app.innerHTML = `<div class="done">Остались только пропущенные: ${passed}.<br><br><button data-again>Показать пропущенные</button> <button data-nav="-1">← назад</button></div>`;
                return;
              }
              const it = items[at];
              const done = total - left;
              app.innerHTML = `
                <div class="counter"><span><b>${at + 1}</b> из ${total}</span><span>осталось <b>${left}</b>${passed ? ` · пропущено ${passed}` : ''}</span></div>
                <div class="bar"><div style="width:${100 * done / total}%"></div></div>
                <div class="where">${esc(it.frame)} · ${it.cell ? 'клетка ' + it.cell : 'Warden'}${it.special ? ' · <span class="special">особый экран</span>' : ''}${it.note ? ' · <span class="special">' + esc(it.note) + '</span>' : ''}${state[at] ? ' · решено: ' + esc(state[at]) : skipped.has(at) ? ' · пропущена' : ''}</div>
                <img src="${it.image}" alt="">
                <div class="reads"><span class="k">1 разметка</span><span>${esc(it.label)}</span><span class="k">2 сеть</span><span>${esc(it.net)}</span></div>
                <div class="btns">
                  <button class="lab" data-c="label"><kbd>1</kbd>права разметка</button>
                  <button class="net" data-c="net"><kbd>2</kbd>права сеть</button>
                  <button data-c="set"><kbd>3</kbd>ни то, ни другое…</button>
                  <button data-c="covered" ${it.cell ? '' : 'disabled'}><kbd>4</kbd>закрыта интерфейсом</button>
                </div>
                <div class="editor" id="editor"></div>
                <div class="nav"><button data-nav="-1">← назад</button><span>пробел или → — пропустить</span><button data-nav="1">пропустить →</button></div>`;
              if (editing) renderEditor();
            }

            // The editor for "neither": starts from the network's reading.
            function openEditor() {
              if (at < 0) return;
              const it = items[at];
              editing = it.cell ? { ...(it.netCell || it.labelCell) } : { warden: it.netWarden || it.labelWarden || 1 };
              render();
            }
            function opt(group, value, current, label) {
              return `<button class="opt ${value === current ? 'on' : ''}" data-g="${group}" data-v="${esc(value)}">${esc(label ?? value)}</button>`;
            }
            function renderEditor() {
              const e = document.getElementById('editor'); e.classList.add('open');
              if (!items[at].cell) {
                e.innerHTML = `<div class="row"><span class="k">Warden в клетке</span>${Array.from({ length: 16 }, (_, i) => opt('warden', i + 1, editing.warden, i + 1)).join('')}</div>
                  <div class="row"><button class="lab" data-save>Enter — сохранить</button><button data-cancel>Esc — отмена</button></div>`;
                return;
              }
              let rows = `<div class="row"><span class="k">Клетка</span>${opt('kind', 'Unopened', editing.kind)}${opt('kind', 'Empty', editing.kind, 'Opened, пусто')}${opt('kind', 'Inmate', editing.kind)}</div>`;
              if (editing.kind === 'Inmate') {
                rows += `<div class="row"><span class="k">Code</span>${CODES.map(c => opt('code', c, editing.code)).join('')}</div>`;
                rows += `<div class="row"><span class="k">Rarity</span>${RARITIES.map(r => opt('rarity', r, editing.rarity)).join('')}</div>`;
                rows += `<div class="row"><span class="k">Флаги</span>${opt('consumable', true, editing.consumable, 'Consumable target')}${opt('incident', true, editing.incident, 'Prison Incident')}${opt('tusk', true, editing.tusk, 'Tusk')}${opt('prey', true, editing.prey, 'Prey')}</div>`;
              } else if (editing.kind === 'Unopened') {
                rows += `<div class="row"><span class="k">Видна rarity</span>${opt('visible', '', editing.visible ?? '', 'нет')}${RARITIES.map(r => opt('visible', r, editing.visible)).join('')}</div>`;
                rows += `<div class="row"><span class="k">Флаги</span>${opt('scratchMarks', true, editing.scratchMarks, 'Scratch Marks')}${opt('incident', true, editing.incident, 'Prison Incident')}${opt('nest', true, editing.nest, 'nest')}</div>`;
              }
              rows += `<div class="row"><button class="lab" data-save>Enter — сохранить</button><button data-cancel>Esc — отмена</button></div>`;
              e.innerHTML = rows;
            }

            async function decide(choice, body) {
              if (at < 0) return;
              if (choice === 'covered' && !items[at].cell) return;
              const r = await fetch(`/decide?item=${items[at].id}&choice=${choice}`, { method: 'POST', body: body ? JSON.stringify(body) : '' });
              if (!r.ok) { alert(await r.text()); return; }
              state[at] = { label: 'разметка', net: 'сеть', set: 'вручную', covered: 'закрыта' }[choice];
              skipped.delete(at);
              advance();
            }
            // The next card neither decided nor skipped: after this one, then from the start; none left: -1.
            function advance() {
              let j = -1;
              for (let i = at + 1; i < total && j < 0; i++) if (pending(i)) j = i;
              for (let i = 0; i < total && j < 0; i++) if (pending(i)) j = i;
              at = j; editing = null; render();
            }
            function skip() { if (at >= 0) { skipped.add(at); advance(); } }
            function back() { at = at <= 0 ? total - 1 : at - 1; editing = null; render(); }

            app.addEventListener('click', e => {
              const b = e.target.closest('button'); if (!b) return;
              if (b.dataset.c === 'set') return openEditor();
              if (b.dataset.c) return decide(b.dataset.c);
              if (b.dataset.nav) return +b.dataset.nav < 0 ? back() : skip();
              if (b.hasAttribute('data-again')) { skipped.clear(); at = -1; return advance(); }
              if (b.dataset.g) {
                const g = b.dataset.g, v = b.dataset.v;
                if (g === 'warden') editing.warden = +v;
                else if (['consumable', 'incident', 'scratchMarks', 'nest', 'tusk', 'prey'].includes(g)) editing[g] = !editing[g];
                else if (g === 'visible') editing.visible = v || null;
                else editing[g] = v;
                if (g === 'kind' && v === 'Inmate') { editing.code ??= CODES[0]; editing.rarity ??= RARITIES[0]; }
                return renderEditor();
              }
              if (b.hasAttribute('data-save')) return decide('set', editing);
              if (b.hasAttribute('data-cancel')) { editing = null; return render(); }
            });
            document.addEventListener('keydown', e => {
              if (editing) {
                if (e.key === 'Enter') decide('set', editing);
                else if (e.key === 'Escape') { editing = null; render(); }
                return;
              }
              const choice = { '1': 'label', '2': 'net', '4': 'covered' }[e.key];
              if (choice) decide(choice);
              else if (e.key === '3') openEditor();
              else if (e.key === ' ' || e.key === 'ArrowRight') { e.preventDefault(); skip(); }
              else if (e.key === 'ArrowLeft') back();
            });
            render();
            </script>
            """;
    }

    // All disagreements on one page, for the record (review/index.html).
    static string ListPage(List<Item> items, int frames, int cells, int wardens)
    {
        var sb = new StringBuilder("""
            <!doctype html><html lang="ru"><meta charset="utf-8"><title>Cell review</title>
            <style>body{background:#1e1f24;color:#e8e8ec;font:14px "Segoe UI",sans-serif;margin:16px} td{padding:4px 10px;vertical-align:middle;border-bottom:1px solid #333}</style>
            """);
        sb.Append($"<h3>{frames} кадров Sandbox · расходятся клеток: {items.Count(i => !i.IsWarden)} из {cells} · Warden: {items.Count(i => i.IsWarden)} из {wardens}</h3>");
        sb.Append("<table><tr><th></th><th>где</th><th>разметка</th><th>сеть</th></tr>");
        foreach (var i in items)
            sb.Append($"<tr><td><img src=\"img/{i.Image}\" width=\"{(i.IsWarden ? 460 : 200)}\"></td><td>{WebUtility.HtmlEncode(i.FrameId)}<br>{(i.IsWarden ? "Warden" : $"клетка {i.Cell}")}{(i.Plain ? "" : "<br><i>особый экран</i>")}</td><td>{WebUtility.HtmlEncode(i.Label)}</td><td>{WebUtility.HtmlEncode(i.Net)}</td><td>{WebUtility.HtmlEncode(i.Note)}</td></tr>");
        sb.Append("</table>");
        return sb.ToString();
    }

    static void Write(HttpListenerContext ctx, string type, string text)
    {
        ctx.Response.ContentType = type;
        ctx.Response.OutputStream.Write(Encoding.UTF8.GetBytes(text));
        ctx.Response.Close();
    }
}
