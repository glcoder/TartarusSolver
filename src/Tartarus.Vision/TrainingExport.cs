using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tartarus.Vision;

/// <summary>
/// Cuts a dataset's frames into the classifiers' training crops, with labels from truth.json:
/// <code>
///   cells/&lt;id&gt;_cNN.png    cells 2-16 with surroundings (CellCrops.Context), cells.csv
///   arrows/&lt;id&gt;_aNN.png   the warden's arrow spot of cells 1-16 (CellCrops.ArrowSpot), arrows.csv
///   slots/&lt;id&gt;_instrumentN.png, _chromaticN.png, _cardN.png   screen slots, slots.csv
/// </code>
/// The Sandbox's place comes from each frame's saved scan (its Loc), so frames of any size and from either
/// scanner cut right. Screen slots are anchored to the window, so they're cut only from frames whose scan
/// agrees with <see cref="ScreenLayout"/> (the frame is the window's client area).
/// </summary>
public static class TrainingExport
{
    private static readonly Tartarus.Rules.Engine Rules = new();

    public sealed record Summary(int Frames, int Cells, int Arrows, int Slots, List<string> Skipped);

    /// <param name="presence">Which frames show the Sandbox; without it, those whose scan read the day counter.</param>
    public static Summary Export(Dataset ds, string outDir, SandboxPresence? presence = null)
    {
        foreach (var sub in new[] { "cells", "arrows", "slots" })
        {
            string d = Path.Combine(outDir, sub);
            if (Directory.Exists(d)) foreach (var f in Directory.EnumerateFiles(d, "*.png")) File.Delete(f);
            Directory.CreateDirectory(d);
        }
        var cells = new StringBuilder("file,frame,cell,kind,code,rarity,consumable,nest,incident,visible_rarity,scratch_marks,tusk,prey,covered,edited,trusted,red\n");
        var arrows = new StringBuilder("file,frame,cell,warden,trusted\n");
        var slots = new StringBuilder("file,frame,kind,index,label,trusted\n");
        var skipped = new List<string>();
        int frames = 0, nCells = 0, nArrows = 0, nSlots = 0;

        foreach (var (file, labels) in Dataset.LoadTruth(ds.TruthPath).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            string framePath = Path.Combine(ds.FramesDir, file), scanPath = Path.Combine(ds.Root, "scans", id + ".json");
            if (!File.Exists(framePath) || !File.Exists(scanPath))
            {
                skipped.Add($"{file}: no frame or scan");
                continue;
            }
            var scan = ScanJson.Parse(File.ReadAllText(scanPath), "file");
            if (scan.Loc is not { } loc)
            {
                skipped.Add($"{file}: scan has no Sandbox place");
                continue;
            }
            // Early layout scans took screens without a Sandbox (instrument picks, story popups) for Sandboxes.
            var frame = ImageIO.Load(framePath);
            if (presence != null ? presence.Score(frame, new ScreenLayout(frame.Width, frame.Height)) < presence.Threshold : scan.Days == null)
            {
                skipped.Add($"{file}: not a Sandbox screen");
                continue;
            }
            var sandbox = EditableSandbox.FromLabels(labels);
            // Frames labeled before visible rarity was labeled: take it from the scan.
            if (labels.VisibleRarity == null && labels.ScratchMarks == null && scan.Cells != null)
                foreach (var c in scan.Cells)
                    if (c.N > 0 && sandbox.Cells[c.N].Kind == CellKind.Unopened && c.VisibleRarity is { } s)
                        (sandbox.Cells[c.N].VisibleRarity, sandbox.Cells[c.N].ScratchMarks) = (s.Rarity, s.ScratchMarks);
            // The small mouth over an Inmate means "has a target": by the rules, from the labeled Sandbox and instruments,
            // rather than the older marks (which missed it on red and orange glows).
            var rules = Rules.FromObservation(GameObservation.From(sandbox), 1);
            for (int n = 2; n <= 16; n++)
                if (sandbox[n].Kind == CellKind.Inmate) sandbox[n].Consumable = Tartarus.Rules.Engine.HasTarget(rules, n - 1);
            // The scanner's reading is a fair label on a plain Sandbox screen (the day counter read); over a
            // Consume pick's red, a fade or a day's banner it isn't, until someone checks it by hand. Nor is the cell
            // networks' own reading: learning from it would only teach them their mistakes again.
            string metaPath = Path.Combine(ds.Root, "meta", id + ".json");
            var meta = File.Exists(metaPath) ? JsonSerializer.Deserialize(File.ReadAllText(metaPath), VisionJson.Default.SampleMeta) : null;
            bool plain = scan.Days != null && meta?.Scanner?.Contains("cnn", StringComparison.Ordinal) != true;
            foreach (int n in meta?.EditedCells ?? []) sandbox[n].Edited = true;
            var carried = meta?.CarriedCells ?? [];
            var marked = meta?.MarkCells ?? [];

            for (int i = 1; i < 16; i++)
            {
                var (u, v) = SandboxGeometry.Ring[i];
                var (x, y) = SandboxGeometry.At(loc, u, v);
                double k = SandboxGeometry.ScaleAt(loc, u, v);
                var label = sandbox.Cells[i];
                string name = $"{id}_c{i + 1:00}.png";
                ImageIO.SavePng(CellCrops.Context(frame, x, y, k), Path.Combine(outDir, "cells", name));
                bool inmate = label.Kind == CellKind.Inmate;
                cells.Append(CultureInfo.InvariantCulture,
                    $"{name},{file},{i + 1},{label.Kind.ToString().ToLowerInvariant()},{(inmate ? Codes.Name(label.Code) : "")},{(inmate ? label.Rarity.ToString() : "")},{B(label.Consumable)},{B(label.Nest)},{B(label.Incident)},{label.VisibleRarity?.ToString() ?? ""},{B(label.ScratchMarks)},{B(label.Tusk)},{B(label.Prey)},{B(label.Covered)},{B(label.Edited)},{B(plain || label.Edited || carried.Contains(i + 1) || marked.Contains(i + 1))},{B(meta?.CarriedCells != null)}\n");
                nCells++;
            }
            for (int i = 0; i < 16; i++)
            {
                string name = $"{id}_a{i + 1:00}.png";
                ImageIO.SavePng(CellCrops.ArrowSpot(frame, loc, i), Path.Combine(outDir, "arrows", name));
                arrows.Append(CultureInfo.InvariantCulture, $"{name},{file},{i + 1},{B(sandbox.Warden == i + 1)},{B(plain || sandbox.SandboxEdited)}\n");
                nArrows++;
            }

            var layout = new ScreenLayout(frame.Width, frame.Height);
            bool client = Math.Abs(layout.Sandbox.S - loc.S) < 0.01 * loc.S && Math.Abs(layout.Sandbox.Tx - loc.Tx) < 4 && Math.Abs(layout.Sandbox.Ty - loc.Ty) < 4;
            if (!client) skipped.Add($"{file}: not a client-area frame (Sandbox at {loc.S:0.###},{loc.Tx:0},{loc.Ty:0}, layout says {layout.Sandbox.S:0.###},{layout.Sandbox.Tx:0},{layout.Sandbox.Ty:0}); slots skipped");
            else
            {
                for (int i = 0; i < ScreenLayout.InstrumentSlots; i++) Slot("instrument", i, ScreenLayout.InstrumentSlot(i), i < sandbox.Instruments.Count ? sandbox.Instruments[i] : "none");
                for (int i = 0; i < ScreenLayout.ChromaticSlots; i++) Slot("chromatic", i, ScreenLayout.ChromaticSlot(i), i < sandbox.Chromatic ? "filled" : "empty");
                // Cards: what OCR read on them, from the saved scan when it has text; else unknown.
                for (int i = 0; i < ScreenLayout.CardCount; i++) Slot("card", i, ScreenLayout.Card(i), "");
            }
            frames++;

            // Slots are trusted on plain screens whoever read the cells (instrument and Chromatic labels are checked apart).
            void Slot(string kind, int i, CanvasRect rect, string label)
            {
                string name = $"{id}_{kind}{i}.png";
                ImageIO.SavePng(layout.Crop(frame, rect), Path.Combine(outDir, "slots", name));
                slots.Append(CultureInfo.InvariantCulture, $"{name},{file},{kind},{i},{label.Replace(',', ' ')},{B(scan.Days != null || sandbox.SandboxEdited)}\n");
                nSlots++;
            }
        }
        File.WriteAllText(Path.Combine(outDir, "cells.csv"), cells.ToString());
        File.WriteAllText(Path.Combine(outDir, "arrows.csv"), arrows.ToString());
        File.WriteAllText(Path.Combine(outDir, "slots.csv"), slots.ToString());
        return new Summary(frames, nCells, nArrows, nSlots, skipped);

        static int B(bool v) => v ? 1 : 0;
    }
}
