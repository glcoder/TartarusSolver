"""Cell and warden-arrow classifiers for the Tartarus board, trained on crops cut by `tartarus-scan export`.

    .venv/Scripts/python tools/train/cellnet.py dataset/train [--out models/cells] [--epochs 40]

cells: one network over a cell with its surroundings (CellCrops.Context, 84x96) plus the cell's number,
with a head per question: kind (empty / unopened / inmate), Code, rarity, Consumable, nest, Prison Incident, the
rarity an Unopened cell shows, Scratch Marks. A head only learns from the cells it applies to (Code and rarity
from Inmates, visible rarity and Scratch Marks from Unopened cells).

arrows: a small network over each cell's arrow spot (CellCrops.ArrowSpot, 48x48) plus the cell's number:
is the warden's arrow there. The warden is the most likely of the 16.

Both are exported to ONNX (inputs "image" float32 NCHW in 0..1 RGB, "cell" one-hot of 16) with a JSON of
class names next to them, for Tartarus.Vision's CnnCellReader.
"""

import argparse
import csv
import json
import random
import time
from collections import Counter, defaultdict
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
from PIL import Image

# The game's terms. Chromatic never stays on the Sandbox (it leaves for the Chromatic panel), so the cells'
# rarities stop at Ultimate.
# "covered": the game's UI (a tooltip) hides the cell; the app keeps its last reading then.
KINDS = ["empty", "unopened", "inmate", "covered"]
CODES = ["Rat", "Wolf", "Lion", "Mammoth"]
RARITIES = ["Normal", "Magic", "Rare", "Legendary", "Ultimate"]
VISIBLE = ["none"] + RARITIES
# tusk, prey: marks of Iron Tongs of Lust: Mammoth and Blood-Stained Trap's Hunt, seen in few runs. A flag with fewer
# than MIN_FLAG_EXAMPLES positives is exported but marked so (flagCounts), and the app doesn't read it.
FLAGS = ["consumable", "nest", "incident", "scratch_marks", "tusk", "prey"]
MIN_FLAG_EXAMPLES = 20
# Training runs on the GPU when there is one (the arrows stay on the CPU: small).
DEV = torch.device("cuda" if torch.cuda.is_available() else "cpu")
IGNORE = -100


def load_image(path):
    return np.asarray(Image.open(path).convert("RGB"), dtype=np.float32) / 255.0


def one_hot(cell):
    v = np.zeros(16, dtype=np.float32)
    v[cell - 1] = 1
    return v


def split_by_frame(rows, val_share, seed):
    """Whole frames go to one side; neighbouring frames of a run look alike, so blocks of 8 go together."""
    frames = sorted({r["frame"] for r in rows})
    blocks = [frames[i:i + 8] for i in range(0, len(frames), 8)]
    random.Random(seed).shuffle(blocks)
    n_val = max(1, round(len(blocks) * val_share)) if len(blocks) > 1 else 0
    val = {f for b in blocks[:n_val] for f in b}
    return [r for r in rows if r["frame"] not in val], [r for r in rows if r["frame"] in val]


# ---------- augmentation (no hue changes: the frame's color is the rarity) ----------

def augment(x, rng):
    """x: HWC float in 0..1. Small shifts and scale, brightness/contrast, sometimes blur."""
    h, w, _ = x.shape
    t = torch.from_numpy(x).permute(2, 0, 1).unsqueeze(0)
    s = 1 + rng.uniform(-0.05, 0.05)
    dx, dy = rng.uniform(-3, 3) * 2 / w, rng.uniform(-3, 3) * 2 / h
    theta = torch.tensor([[[s, 0, dx], [0, s, dy]]], dtype=torch.float32)
    grid = F.affine_grid(theta, t.shape, align_corners=False)
    t = F.grid_sample(t, grid, padding_mode="border", align_corners=False)
    t = t * rng.uniform(0.8, 1.2) + rng.uniform(-0.06, 0.06)
    mean = t.mean()
    t = (t - mean) * rng.uniform(0.85, 1.15) + mean
    if rng.random() < 0.25:
        k = torch.tensor([[1, 2, 1], [2, 4, 2], [1, 2, 1]], dtype=torch.float32) / 16
        t = F.conv2d(t, k.expand(3, 1, 3, 3), padding=1, groups=3)
    return t.clamp(0, 1).squeeze(0)


# ---------- semi-synthetic marks ----------

PREY_ICON = Path(__file__).resolve().parents[2] / "game_art/s16/Art/UIV2/S16Gameplay/UI/Textures/NoAtlas/UI_T_V2_S16BoardCell_039.png"


def synthetic_prey(tr, n, rng, icon_path=PREY_ICON):
    """n Inmate cells without Prey, copied with the game's Prey icon laid where the game draws it (top left at 19,62
    of the 84x96 crop, about half the icon's size, fitted on the real Prey cells), a little off in size, place and
    brightness (the mark dims under a popup like the rest of the frame); appended to the training set as Prey."""
    images, cells, kinds, codes, rarities, visible, flags = tr
    p = FLAGS.index("prey")
    pool = [i for i in range(len(kinds)) if kinds[i] == KINDS.index("inmate") and flags[i, p] == 0]
    if not pool or n <= 0:
        return tr
    icon = Image.open(icon_path).convert("RGBA")
    picked = [rng.choice(pool) for _ in range(n)]
    made = []
    for i in picked:
        s = rng.uniform(0.46, 0.52)
        w, h = round(icon.width * s), round(icon.height * s)
        mark = np.asarray(icon.resize((w, h), Image.LANCZOS), dtype=np.float32) / 255.0
        x, y = 19 + rng.randint(-2, 2), 62 + rng.randint(-2, 2)
        img = images[i].copy()
        alpha = mark[:, :, 3:4]
        region = img[y:y + h, x:x + w]
        img[y:y + h, x:x + w] = region * (1 - alpha) + mark[:, :, :3] * rng.uniform(0.55, 1.05) * alpha
        made.append(np.clip(img, 0, 1))
    f = flags[picked].copy()
    f[:, p] = 1
    print(f"semi-synthetic Prey: {n} cells")
    return (np.concatenate([images, np.stack(made)]), np.concatenate([cells, cells[picked]]), np.concatenate([kinds, kinds[picked]]),
            np.concatenate([codes, codes[picked]]), np.concatenate([rarities, rarities[picked]]), np.concatenate([visible, visible[picked]]),
            np.concatenate([flags, f]))


# ---------- models ----------

def block(cin, cout):
    return nn.Sequential(
        nn.Conv2d(cin, cout, 3, padding=1, bias=False), nn.BatchNorm2d(cout), nn.ReLU(inplace=True),
        nn.Conv2d(cout, cout, 3, padding=1, bias=False), nn.BatchNorm2d(cout), nn.ReLU(inplace=True),
    )


class CellNet(nn.Module):
    def __init__(self):
        super().__init__()
        self.features = nn.Sequential(block(3, 24), nn.MaxPool2d(2), block(24, 40), nn.MaxPool2d(2),
                                      block(40, 64), nn.MaxPool2d(2), block(64, 96))
        self.fc = nn.Sequential(nn.Linear(96 * 2 + 16, 128), nn.ReLU(inplace=True), nn.Dropout(0.2))
        self.kind = nn.Linear(128, len(KINDS))
        self.code = nn.Linear(128, len(CODES))
        self.rarity = nn.Linear(128, len(RARITIES))
        self.visible = nn.Linear(128, len(VISIBLE))
        self.flags = nn.Linear(128, len(FLAGS))

    def forward(self, image, cell):
        f = self.features(image)
        # Mean and max over the picture: max keeps small things (the "!", a nest) that the mean averages away.
        f = torch.cat([f.mean((2, 3)), f.amax((2, 3)), cell], 1)
        h = self.fc(f)
        return self.kind(h), self.code(h), self.rarity(h), self.visible(h), self.flags(h)


class ArrowNet(nn.Module):
    def __init__(self):
        super().__init__()
        self.features = nn.Sequential(block(3, 16), nn.MaxPool2d(2), block(16, 32), nn.MaxPool2d(2), block(32, 48))
        self.fc = nn.Sequential(nn.Linear(48 * 2 + 16, 64), nn.ReLU(inplace=True), nn.Linear(64, 1))

    def forward(self, image, cell):
        f = self.features(image)
        return self.fc(torch.cat([f.mean((2, 3)), f.amax((2, 3)), cell], 1))


# ---------- data ----------

def cell_targets(r):
    if r["covered"] == "1":
        return KINDS.index("covered"), IGNORE, IGNORE, IGNORE, [-1] * len(FLAGS)
    kind = KINDS.index(r["kind"]) if r["kind"] in KINDS else IGNORE
    inmate, unopened = r["kind"] == "inmate", r["kind"] == "unopened"
    code = CODES.index(r["code"]) if inmate and r["code"] in CODES else IGNORE
    rarity = RARITIES.index(r["rarity"]) if inmate and r["rarity"] in RARITIES else IGNORE
    visible = (VISIBLE.index(r["visible_rarity"]) if r["visible_rarity"] in VISIBLE else 0) if unopened else IGNORE
    # Flags as 0/1, or -1 where the question doesn't apply.
    # Blank on the Consume pick's red frames (carry-red doesn't know the small mouth there): unknown.
    consumable = int(r["consumable"]) if inmate and r["consumable"] != "" else -1
    nest = int(r["nest"]) if unopened else -1
    incident = int(r["incident"]) if r["kind"] in ("unopened", "inmate") else -1
    scratch_marks = int(r["scratch_marks"]) if unopened else -1
    # Exports before these marks had no columns for them: unknown there.
    tusk = int(r["tusk"]) if inmate and r.get("tusk") not in (None, "") else -1
    prey = int(r["prey"]) if inmate and r.get("prey") not in (None, "") else -1
    return kind, code, rarity, visible, [consumable, nest, incident, scratch_marks, tusk, prey]


def load_cells(root, rows):
    images = np.stack([load_image(root / "cells" / r["file"]) for r in rows])
    cells = np.stack([one_hot(int(r["cell"])) for r in rows])
    t = [cell_targets(r) for r in rows]
    kinds, codes, rarities, visible = (np.array([x[i] for x in t], dtype=np.int64) for i in range(4))
    flags = np.array([x[4] for x in t], dtype=np.float32)
    return images, cells, kinds, codes, rarities, visible, flags


def batches(n, size, shuffle, rng):
    idx = list(range(n))
    if shuffle:
        rng.shuffle(idx)
    for i in range(0, n, size):
        yield idx[i:i + size]


def to_tensor(images, idx, rng=None):
    if rng is None:
        return torch.from_numpy(images[idx]).permute(0, 3, 1, 2).contiguous()
    return torch.stack([augment(images[i], rng) for i in idx])


# ---------- training ----------

def is_red(r):
    return r.get("red", "0") == "1"


def train_cells(root, out, epochs, seed, red_weight=4, synthetic=0):
    # Only labels worth learning: plain board screens or cells checked by hand (see TrainingExport).
    rows = [r for r in csv.DictReader(open(root / "cells.csv", newline="")) if r.get("trusted", "1") == "1"]
    train_rows, val_rows = split_by_frame(rows, 0.15, seed)
    # The Consume pick's red frames (labeled by carry-red): few, so each counts red_weight times; 0 leaves them out
    # of training (the split stays the same, so runs with and without compare on the same val frames).
    train_rows = [r for r in train_rows if not is_red(r)] + [r for r in train_rows if is_red(r)] * red_weight
    print(f"red cells: {sum(is_red(r) for r in train_rows)} train (x{red_weight}), {sum(is_red(r) for r in val_rows)} val")
    print(f"cells: {len(train_rows)} train, {len(val_rows)} val; kinds {Counter(r['kind'] for r in rows)}")
    tr, va = load_cells(root, train_rows), load_cells(root, val_rows)

    torch.manual_seed(seed)
    rng = random.Random(seed)
    tr = synthetic_prey(tr, synthetic, rng)
    n_train = len(tr[2])
    print(f"real Prey cells: {sum(r.get('prey') == '1' for r in train_rows)} train, {sum(r.get('prey') == '1' for r in val_rows)} val")
    model = CellNet().to(DEV)
    print(f"CellNet: {sum(p.numel() for p in model.parameters()):,} parameters")
    opt = torch.optim.AdamW(model.parameters(), lr=2e-3, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=3e-3, total_steps=epochs * ((n_train + 63) // 64))

    # Rare classes weigh more (the square root of their rarity, so they don't swamp the rest): a white "?"
    # (visible Normal), Ultimate, an Incident are few next to plain Unopened cells and Normal Inmates.
    def class_weights(y, n_classes):
        counts = np.bincount(y[y != IGNORE], minlength=n_classes).astype(np.float64)
        w = np.where(counts > 0, np.sqrt(counts.sum() / (n_classes * np.maximum(counts, 1))), 1.0)
        return torch.tensor(np.clip(w, 0.5, 8.0), dtype=torch.float32, device=DEV)

    # The kind: only "covered" weighs more (few, and checked by hand); "Empty" stays at 1, most of its labels being
    # the teacher missing a padlock.
    kind_w = class_weights(tr[2], len(KINDS))
    kind_w[KINDS.index("empty")] = 1.0
    kind_w[KINDS.index("unopened")] = kind_w[KINDS.index("inmate")] = 1.0
    weights = [kind_w, class_weights(tr[3], len(CODES)), class_weights(tr[4], len(RARITIES)), class_weights(tr[5], len(VISIBLE))]
    flags_known = tr[6] >= 0
    pos = ((tr[6] == 1) & flags_known).sum(0)
    neg = ((tr[6] == 0) & flags_known).sum(0)
    pos_weight = torch.tensor(np.clip(np.sqrt(neg / np.maximum(pos, 1)), 1.0, 8.0), dtype=torch.float32, device=DEV)
    print("weights: " + ", ".join(f"{n} {[round(float(x), 2) for x in w]}" for n, w in zip(["kind", "code", "rarity", "visible"], weights)) + f", flags {[round(float(x), 2) for x in pos_weight]}")

    def losses(outputs, kinds, codes, rarities, visible, flags):
        k, b, t, s, f = outputs
        loss = F.cross_entropy(k, kinds, weight=weights[0], ignore_index=IGNORE)
        for logits, y, w in ((b, codes, weights[1]), (t, rarities, weights[2]), (s, visible, weights[3])):
            if (y != IGNORE).any():
                loss = loss + F.cross_entropy(logits, y, weight=w, ignore_index=IGNORE)
        mask = flags >= 0
        if mask.any():
            per = F.binary_cross_entropy_with_logits(f, flags.clamp(min=0), pos_weight=pos_weight, reduction="none")
            loss = loss + per[mask].mean()
        return loss

    best = None
    for epoch in range(epochs):
        model.train()
        t0, total, n = time.time(), 0.0, 0
        for idx in batches(n_train, 64, True, rng):
            x = to_tensor(tr[0], idx, rng).to(DEV, non_blocking=True)
            ys = [torch.from_numpy(a[idx]).to(DEV) for a in tr[2:]]
            loss = losses(model(x, torch.from_numpy(tr[1][idx]).to(DEV)), *ys)
            opt.zero_grad()
            loss.backward()
            opt.step()
            sched.step()
            total += loss.item() * len(idx)
            n += len(idx)
        if val_rows and (epoch % 5 == 4 or epoch == epochs - 1):
            acc = evaluate_cells(model, va)
            print(f"epoch {epoch + 1}: loss {total / n:.4f} ({time.time() - t0:.1f}s) val " + " ".join(f"{k} {v}" for k, v in acc.items()))
        else:
            print(f"epoch {epoch + 1}: loss {total / n:.4f} ({time.time() - t0:.1f}s)")
    if val_rows:
        report_errors(model, va, val_rows)
        for name, keep in (("plain", lambda r: not is_red(r)), ("red", is_red)):
            part = [r for r in val_rows if keep(r)]
            if part:
                acc = evaluate_cells(model, load_cells(root, part))
                print(f"val {name} ({len(part)} cells): " + " ".join(f"{k} {v}" for k, v in acc.items()))
    flag_counts = {name: int((tr[6][:, i] == 1).sum()) for i, name in enumerate(FLAGS)}
    print("flag examples:", flag_counts)
    export(model, out / "cellnet", (3, 96, 84), {
        "kinds": KINDS, "codes": CODES, "rarities": RARITIES, "visible": VISIBLE, "flags": FLAGS,
        "flagCounts": flag_counts, "minFlagExamples": MIN_FLAG_EXAMPLES,
        "outputs": ["kind", "code", "rarity", "visible", "flags"],
    }, ["kind", "code", "rarity", "visible", "flags"])


@torch.no_grad()
def predict_cells(model, data):
    model.eval()
    images, cells = data[0], data[1]
    outs = [[o.cpu() for o in model(to_tensor(images, idx).to(DEV), torch.from_numpy(cells[idx]).to(DEV))] for idx in batches(len(images), 256, False, None)]
    return [torch.cat([o[i] for o in outs]) for i in range(5)]


def evaluate_cells(model, data):
    k, b, t, s, f = predict_cells(model, data)
    _, _, kinds, codes, rarities, visible, flags = data
    acc = {}
    for name, logits, y in (("kind", k, kinds), ("code", b, codes), ("rarity", t, rarities), ("visible", s, visible)):
        y = torch.from_numpy(y)
        m = y != IGNORE
        acc[name] = f"{(logits.argmax(1)[m] == y[m]).float().mean().item():.3f}/{int(m.sum())}" if m.any() else "-"
    fl = torch.from_numpy(flags)
    for i, name in enumerate(FLAGS):
        m = fl[:, i] >= 0
        acc[name] = f"{((f[m, i] > 0).float() == fl[m, i]).float().mean().item():.3f}/{int(m.sum())}" if m.any() else "-"
    return acc


def report_errors(model, data, rows, limit=30):
    k, b, t, s, f = predict_cells(model, data)
    errs = []
    for i, r in enumerate(rows):
        want = cell_targets(r)
        got_kind = int(k[i].argmax())
        bad = []
        if want[0] != IGNORE and got_kind != want[0]:
            bad.append(f"kind {KINDS[got_kind]} want {r['kind']}")
        if want[1] != IGNORE and int(b[i].argmax()) != want[1]:
            bad.append(f"code {CODES[int(b[i].argmax())]} want {r['code']}")
        if want[2] != IGNORE and int(t[i].argmax()) != want[2]:
            bad.append(f"rarity {RARITIES[int(t[i].argmax())]} want {r['rarity']}")
        if want[3] != IGNORE and int(s[i].argmax()) != want[3]:
            bad.append(f"visible {VISIBLE[int(s[i].argmax())]} want {VISIBLE[want[3]]}")
        for j, name in enumerate(FLAGS):
            if want[4][j] >= 0 and int(f[i, j] > 0) != want[4][j]:
                bad.append(f"{name} {int(f[i, j] > 0)} want {want[4][j]}")
        if bad:
            errs.append(f"  {r['file']}: " + ", ".join(bad))
    print(f"val disagreements with labels: {len(errs)} of {len(rows)} cells")
    for e in errs[:limit]:
        print(e)


def train_arrows(root, out, epochs, seed):
    rows = [r for r in csv.DictReader(open(root / "arrows.csv", newline="")) if r.get("trusted", "1") == "1"]
    train_rows, val_rows = split_by_frame(rows, 0.15, seed)
    print(f"arrows: {len(train_rows)} train, {len(val_rows)} val; positives {sum(r['warden'] == '1' for r in rows)}")

    def load(rs):
        return (np.stack([load_image(root / "arrows" / r["file"]) for r in rs]),
                np.stack([one_hot(int(r["cell"])) for r in rs]),
                np.array([float(r["warden"]) for r in rs], dtype=np.float32))

    tr = load(train_rows)
    torch.manual_seed(seed)
    rng = random.Random(seed)
    model = ArrowNet()
    print(f"ArrowNet: {sum(p.numel() for p in model.parameters()):,} parameters")
    opt = torch.optim.AdamW(model.parameters(), lr=2e-3, weight_decay=1e-4)
    pos_weight = torch.tensor([max(1.0, (tr[2] == 0).sum() / max(1, (tr[2] == 1).sum()) / 3)])
    for epoch in range(epochs):
        model.train()
        total, n = 0.0, 0
        for idx in batches(len(train_rows), 64, True, rng):
            logits = model(to_tensor(tr[0], idx, rng), torch.from_numpy(tr[1][idx])).squeeze(1)
            loss = F.binary_cross_entropy_with_logits(logits, torch.from_numpy(tr[2][idx]), pos_weight=pos_weight)
            opt.zero_grad()
            loss.backward()
            opt.step()
            total += loss.item() * len(idx)
            n += len(idx)
        if epoch % 5 == 4 or epoch == epochs - 1:
            print(f"arrows epoch {epoch + 1}: loss {total / n:.4f}")
    if val_rows:
        va = load(val_rows)
        model.eval()
        with torch.no_grad():
            p = model(to_tensor(va[0], list(range(len(val_rows)))), torch.from_numpy(va[1])).squeeze(1)
        # Per frame: the warden is the highest-scoring spot, if any scores above zero.
        by_frame = defaultdict(list)
        for i, r in enumerate(val_rows):
            by_frame[r["frame"]].append((float(p[i]), int(r["cell"]), r["warden"] == "1"))
        right = 0
        for spots in by_frame.values():
            best = max(spots)
            want = next((c for _, c, w in spots if w), 0)
            got = best[1] if best[0] > 0 else 0
            right += got == want
        print(f"arrows val: warden right in {right} of {len(by_frame)} frames")
    export(model, out / "arrownet", (3, 48, 48), {"outputs": ["arrow"]}, ["arrow"])


def export(model, path, shape, meta, outputs):
    model = model.cpu()
    model.eval()
    path.parent.mkdir(parents=True, exist_ok=True)
    image = torch.zeros(1, *shape)
    cell = torch.zeros(1, 16)
    torch.onnx.export(model, (image, cell), str(path.with_suffix(".onnx")), input_names=["image", "cell"],
                      output_names=outputs, dynamic_axes={"image": {0: "n"}, "cell": {0: "n"}, **{o: {0: "n"} for o in outputs}},
                      opset_version=17, dynamo=False)
    meta = dict(meta, input={"image": list(shape), "cell": 16, "scale": "RGB 0..1"}, trained=time.strftime("%Y-%m-%d %H:%M"))
    path.with_suffix(".json").write_text(json.dumps(meta, indent=2))
    print(f"exported {path.with_suffix('.onnx')}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("data", type=Path, help="folder made by `tartarus-scan export` (cells.csv, arrows.csv, crops)")
    ap.add_argument("--out", type=Path, default=Path("models/cells"))
    ap.add_argument("--epochs", type=int, default=40)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--only", choices=["cells", "arrows"])
    ap.add_argument("--red-weight", type=int, default=4, help="times each red (Consume pick) cell counts; 0 leaves them out")
    ap.add_argument("--threads", type=int, default=4, help="CPU threads (leave some to the game)")
    ap.add_argument("--synthetic-prey", type=int, default=0, help="Inmate cells given the game's Prey icon, added to training (the real ones are few)")
    a = ap.parse_args()
    torch.set_num_threads(a.threads)
    if a.only != "arrows":
        train_cells(a.data, a.out, a.epochs, a.seed, a.red_weight, a.synthetic_prey)
    if a.only != "cells":
        train_arrows(a.data, a.out, max(10, a.epochs // 2), a.seed)


if __name__ == "__main__":
    main()
