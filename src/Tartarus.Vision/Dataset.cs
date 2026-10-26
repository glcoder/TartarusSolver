using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tartarus.Vision;

/// <summary>What was known when a sample was saved.</summary>
public sealed class SampleMeta
{
    public string Id { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Window { get; set; }
    public string? Scanner { get; set; }
    public double ScanMs { get; set; }
    /// <summary>1-based cells changed by hand.</summary>
    public List<int> EditedCells { get; set; } = [];
    /// <summary>1-based cells of a Consume pick's red frame labeled from the plain frame before it (tartarus-scan carry-red).</summary>
    public List<int>? CarriedCells { get; set; }
    /// <summary>1-based cells whose marks (Rat Nest, Tusk, Prey, Scratch Marks) were found by matching the game's own mark icons.</summary>
    public List<int>? MarkCells { get; set; }
    public bool SandboxEdited { get; set; }
    /// <summary>False for frames kept as "no Sandbox here" examples; they have no entry in truth.json.</summary>
    public bool HasSandbox { get; set; }
    /// <summary>Saved by the app on its own (the Sandbox changed), not by the player.</summary>
    public bool Auto { get; set; }
}

/// <summary>
/// A folder of labeled screenshots:
/// <code>
///   frames/&lt;id&gt;.png      the captured frame, lossless
///   scans/&lt;id&gt;.json      what the scanner read (raw scan.js JSON)
///   meta/&lt;id&gt;.json       SampleMeta
///   truth.json            labels of every frame, keyed "&lt;id&gt;.png" (SandboxLabels)
///   train/                (made by TrainingExport: classifier crops and their labels)
///   picks/&lt;id&gt;.png, .json  Torture Instrument pick screens and what was read on them (SavePick)
/// </code>
/// </summary>
public sealed class Dataset(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string TruthPath => Path.Combine(Root, "truth.json");
    public string FramesDir => Path.Combine(Root, "frames");

    private const string About =
        "Hand labels for the frames in frames/, in the game's terms. Cells are 1-16 (1 is the Interrogation Room); inmates are Code/Rarity by name (\"Wolf/Rare\"): Codes Rat, Wolf, Lion, Mammoth; rarities Normal, Magic, Rare, Legendary, Ultimate. Destinations are listed in card order, left to right.";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string NewId(DateTimeOffset t) => t.ToLocalTime().ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);

    public string PicksDir => Path.Combine(Root, "picks");

    /// <summary>
    /// Saves a Torture Instrument pick screen to picks/: &lt;id&gt;.png and &lt;id&gt;.json (the scan, with what the cards
    /// read). Kept apart from the Sandbox frames: no labels, not exported. Returns the id.
    /// </summary>
    public string SavePick(Frame frame, ScanResult scan)
    {
        string id = NewId(frame.Timestamp);
        Directory.CreateDirectory(PicksDir);
        ImageIO.SavePng(frame, Path.Combine(PicksDir, id + ".png"));
        WriteText(Path.Combine(PicksDir, id + ".json"), scan.RawJson.Length > 0 ? scan.RawJson : JsonSerializer.Serialize(scan, VisionJson.Default.ScanResult));
        return id;
    }

    /// <summary>Saves frame, scan and labels. Returns the sample id. Training crops come from <see cref="TrainingExport"/>.</summary>
    public string Save(Frame frame, ScanResult scan, EditableSandbox sandbox, string? window, bool auto = false)
    {
        string id = NewId(frame.Timestamp);
        Directory.CreateDirectory(FramesDir);
        ImageIO.SavePng(frame, Path.Combine(FramesDir, id + ".png"));
        WriteText(Path.Combine(Root, "scans", id + ".json"), scan.RawJson.Length > 0 ? scan.RawJson : JsonSerializer.Serialize(scan, VisionJson.Default.ScanResult));
        var meta = new SampleMeta
        {
            Id = id,
            SavedAt = DateTimeOffset.Now,
            CapturedAt = frame.Timestamp,
            Width = frame.Width,
            Height = frame.Height,
            Window = window,
            Scanner = scan.Source,
            ScanMs = scan.Ms,
            EditedCells = sandbox.Cells.Where(c => c.Edited).Select(c => c.N).ToList(),
            SandboxEdited = sandbox.SandboxEdited,
            HasSandbox = scan.Ok || sandbox.AnyEdited,
            Auto = auto,
        };
        WriteText(Path.Combine(Root, "meta", id + ".json"), JsonSerializer.Serialize(meta, VisionJson.Default.SampleMeta));
        // A frame without a Sandbox (another screen, a popup) is kept as a negative example, without labels.
        if (meta.HasSandbox) SetLabels(id + ".png", sandbox.ToLabels());
        return id;
    }

    public void SetLabels(string file, SandboxLabels labels)
    {
        var truth = LoadTruthNode();
        truth[file] = JsonSerializer.SerializeToNode(labels, VisionJson.Default.SandboxLabels);
        WriteText(TruthPath, truth.ToJsonString(Indented));
    }

    /// <summary>Labels of every file in a truth.json (this dataset's, or tools/scan-truth.json's older format).</summary>
    public static Dictionary<string, SandboxLabels> LoadTruth(string path)
    {
        var result = new Dictionary<string, SandboxLabels>();
        if (!File.Exists(path)) return result;
        var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [];
        foreach (var (key, value) in node)
        {
            if (key.StartsWith('_') || value is not JsonObject) continue;
            // Labels written before the game's terms were adopted are read as if they had been.
            var labels = SandboxLabels.Upgrade(value.AsObject()).Deserialize(VisionJson.Default.SandboxLabels);
            if (labels != null) result[key] = labels;
        }
        return result;
    }

    private JsonObject LoadTruthNode()
    {
        if (File.Exists(TruthPath) && JsonNode.Parse(File.ReadAllText(TruthPath)) is JsonObject o)
        {
            // Rewritten in today's terms whenever a label is saved.
            foreach (var (key, value) in o) if (!key.StartsWith('_') && value is JsonObject entry) SandboxLabels.Upgrade(entry);
            o["_about"] = About;
            return o;
        }
        return new JsonObject { ["_about"] = About };
    }

    /// <summary>Labels of one frame from truth.json, or null.</summary>
    public SandboxLabels? LabelsOf(string file) => LoadTruth(TruthPath).GetValueOrDefault(file);

    /// <summary>
    /// Records that someone checked a frame's cell (1-16) or its Sandbox-level fields by hand: meta's EditedCells /
    /// SandboxEdited, which training trusts and review leaves alone.
    /// </summary>
    public void MarkChecked(string id, int? cell = null, bool sandbox = false)
    {
        string path = Path.Combine(Root, "meta", id + ".json");
        var meta = File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), VisionJson.Default.SampleMeta) ?? new SampleMeta { Id = id } : new SampleMeta { Id = id, HasSandbox = true };
        if (cell is int n && !meta.EditedCells.Contains(n)) meta.EditedCells.Add(n);
        if (sandbox) meta.SandboxEdited = true;
        WriteText(path, JsonSerializer.Serialize(meta, VisionJson.Default.SampleMeta));
    }

    /// <summary>A frame's meta, or null.</summary>
    public SampleMeta? MetaOf(string id)
    {
        string path = Path.Combine(Root, "meta", id + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), VisionJson.Default.SampleMeta) : null;
    }

    /// <summary>Rewrites truth.json in today's terms (see <see cref="SandboxLabels.Upgrade"/>), keeping the old file as truth.json.bak.</summary>
    public int RewriteTruth()
    {
        if (!File.Exists(TruthPath)) return 0;
        File.Copy(TruthPath, TruthPath + ".bak", overwrite: true);
        var truth = LoadTruthNode();
        WriteText(TruthPath, truth.ToJsonString(Indented));
        return truth.Count(kv => !kv.Key.StartsWith('_'));
    }

    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write-then-rename so a crash never leaves a half-written truth.json.
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
