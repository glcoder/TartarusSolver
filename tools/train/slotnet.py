"""Classifiers for the screen's slots, trained on crops cut by `tartarus-scan export` (slots.csv).

    .venv/Scripts/python tools/train/slotnet.py dataset/train [--out models/slots] [--epochs 40]

chromatic: is a Chromatic Prisoner slot filled (60x80 crop). The panel holds the run's Chromatic Inmates, at most
10, filled in order; only how many counts. Labels from dataset/chromatic_labels.json ("<frame id>/<slot>": filled,
empty or hidden) when it exists.
instrument: which Torture Instrument a slot under "Torture Instrument" holds, or none (63x104 crop).

Both are exported to ONNX (input "image" float32 NCHW, RGB 0..1) with a JSON of class names, for
Tartarus.Vision's SlotReader.
"""

import argparse
import csv
import json
import os
import random
import time
from collections import Counter
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

from cellnet import augment, batches, block, load_image, split_by_frame


class SlotNet(nn.Module):
    def __init__(self, classes):
        super().__init__()
        self.features = nn.Sequential(block(3, 16), nn.MaxPool2d(2), block(16, 32), nn.MaxPool2d(2), block(32, 48))
        self.fc = nn.Sequential(nn.Linear(48 * 2, 64), nn.ReLU(inplace=True), nn.Dropout(0.2), nn.Linear(64, classes))

    def forward(self, image):
        f = self.features(image)
        return self.fc(torch.cat([f.mean((2, 3)), f.amax((2, 3))], 1))


def pasted_icons(images, labels, classes, icon_dir, per_class, rng):
    """Each instrument's game icon (models/instruments/<id>.png) pasted into an empty slot frame, so that the network
    knows the instruments nobody has held yet. The frame is the 10th percentile of the filled slots' crops (the icons
    differ, the frame doesn't); the icon is ~75 canvas units on a side, centred, as fitted on real slots."""
    from PIL import Image
    filled = images[labels != classes.index("none")]
    frame = np.percentile(filled, 10, axis=0)
    h, w, _ = frame.shape
    xs, ys = [], []
    for c, name in enumerate(classes):
        path = icon_dir / f"{name}.png"
        if not path.exists():
            continue
        icon = Image.open(path).convert("RGBA")
        for _ in range(per_class):
            side = rng.randint(72, 78)
            ic = np.asarray(icon.resize((side, side), Image.BILINEAR), dtype=np.float32) / 255.0
            cx, cy = w / 2 + rng.uniform(-1.5, 1.5), h / 2 + rng.uniform(-1.5, 1.5)
            x0, y0 = int(round(cx - side / 2)), int(round(cy - side / 2))
            bg = frame * rng.uniform(0.8, 1.2)
            sx0, sy0, sx1, sy1 = max(0, -x0), max(0, -y0), min(side, w - x0), min(side, h - y0)
            a = ic[sy0:sy1, sx0:sx1, 3:]
            region = bg[y0 + sy0:y0 + sy1, x0 + sx0:x0 + sx1]
            region[:] = region * (1 - a) + ic[sy0:sy1, sx0:sx1, :3] * rng.uniform(0.75, 1.15) * a
            xs.append(np.clip(bg + np.random.default_rng(rng.getrandbits(32)).normal(0, 0.01, bg.shape), 0, 1).astype(np.float32))
            ys.append(c)
    return np.stack(xs), np.array(ys, dtype=np.int64)


TOOLTIP_WORDS = ("Condition Effect daily destination option there is a chance to trigger after Consuming Consumed opening opens "
                 "Randomly cell cells Inmate Inmates rarity Rarity Tier Tiers additional this the of when moving tiles in a single "
                 "movement replacing Replace guaranteed to be Higher-Tier Lower-Tier equal Mammoth Lion Wolf Rat 1 2 3 40% 35%").split()


def tooltips(images, n, rng):
    """Slots under a tooltip (the game's hover text over the panel): a dark, nearly opaque panel with lines of text,
    over the whole slot or from a random edge, drawn on random slot crops. They teach the "hidden" class: a slot that
    shows no instrument to read, so the app keeps what it read there before."""
    from PIL import Image, ImageDraw, ImageFont
    fonts = [ImageFont.truetype(str(Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts" / "segoeui.ttf"), size) for size in (14, 16, 18, 20, 22, 24)]
    colors = [(235, 230, 220)] * 3 + [(150, 145, 140), (220, 90, 80), (90, 200, 210)]
    out = []
    for _ in range(n):
        base = images[rng.randrange(len(images))]
        h, w, _ = base.shape
        panel = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        d = ImageDraw.Draw(panel)
        x0 = rng.choice([0, 0, 0, rng.randint(-5, 35)])
        y0 = rng.choice([0, 0, rng.randint(-10, 60)])
        shade = rng.randint(22, 50)
        d.rectangle([x0, y0, w, h], fill=(shade, shade - 2, max(0, shade - 5), rng.randint(215, 252)))
        font = rng.choice(fonts)
        y = y0 + rng.randint(-15, 8)
        while y < h:
            d.text((x0 + rng.randint(-30, 6), y), " ".join(rng.choice(TOOLTIP_WORDS) for _ in range(3)), font=font, fill=rng.choice(colors))
            y += font.size * rng.uniform(1.3, 1.6)
        im = Image.alpha_composite(Image.fromarray((base * 255).astype(np.uint8)).convert("RGBA"), panel).convert("RGB")
        out.append(np.asarray(im, dtype=np.float32) / 255.0)
    return np.stack(out)


def train(root, out, kind, classes_of, epochs, seed, names=None, icons=None, holdout=()):
    rows = [r for r in csv.DictReader(open(root / "slots.csv", newline="")) if r["kind"] == kind]
    if names is None:
        rows = [r for r in rows if r.get("trusted", "1") == "1"]
    else:
        # Named by hand (tools/train/instruments.py), whatever the scanner said. A slot nobody could see ("hidden") is
        # kept where the classes have "hidden", left out elsewhere.
        rows = [dict(r, label=names[k]) for r in rows if (k := f"{r['frame'][:-4]}/{r['index']}") in names]
    classes = classes_of(rows)
    rows = [r for r in rows if r["label"] in classes]
    train_rows, val_rows = split_by_frame(rows, 0.15, seed)
    # --holdout: real crops of these go to validation only, to see how well the pasted icons alone teach them.
    val_rows += [r for r in train_rows if r["label"] in holdout]
    train_rows = [r for r in train_rows if r["label"] not in holdout]
    print(f"{kind}: {len(train_rows)} train, {len(val_rows)} val; {Counter(r['label'] for r in rows).most_common()}")
    if not train_rows:
        return

    def load(rs):
        return (np.stack([load_image(root / "slots" / r["file"]) for r in rs]),
                np.array([classes.index(r["label"]) for r in rs], dtype=np.int64))

    tr = load(train_rows)
    torch.manual_seed(seed)
    rng = random.Random(seed)
    if icons is not None:
        px, py = pasted_icons(tr[0], tr[1], classes, icons, 150, rng)
        print(f"{kind}: +{len(py)} pasted icons of {len(set(py.tolist()))} instruments")
        tr = (np.concatenate([tr[0], px]), np.concatenate([tr[1], py]))
        train_rows = train_rows + [None] * len(py)
    if "hidden" in classes:
        hx = tooltips(tr[0], 900, rng)
        print(f"{kind}: +{len(hx)} slots under a drawn tooltip")
        tr = (np.concatenate([tr[0], hx]), np.concatenate([tr[1], np.full(len(hx), classes.index("hidden"), dtype=np.int64)]))
        train_rows = train_rows + [None] * len(hx)
    model = SlotNet(len(classes))
    counts = np.bincount(tr[1], minlength=len(classes)).astype(np.float64)
    weight = torch.tensor(np.clip(np.where(counts > 0, np.sqrt(counts.sum() / (len(classes) * np.maximum(counts, 1))), 1.0), 0.5, 8.0), dtype=torch.float32)
    opt = torch.optim.AdamW(model.parameters(), lr=2e-3, weight_decay=1e-4)
    for epoch in range(epochs):
        model.train()
        total, n, t0 = 0.0, 0, time.time()
        for idx in batches(len(train_rows), 64, True, rng):
            x = torch.stack([augment(tr[0][i], rng) for i in idx])
            loss = F.cross_entropy(model(x), torch.from_numpy(tr[1][idx]), weight=weight)
            opt.zero_grad()
            loss.backward()
            opt.step()
            total += loss.item() * len(idx)
            n += len(idx)
        if epoch % 10 == 9 or epoch == epochs - 1:
            print(f"{kind} epoch {epoch + 1}: loss {total / n:.4f} ({time.time() - t0:.1f}s)")
    if val_rows:
        va = load(val_rows)
        model.eval()
        with torch.no_grad():
            pred = model(torch.from_numpy(va[0]).permute(0, 3, 1, 2).contiguous()).argmax(1).numpy()
        right = int((pred == va[1]).sum())
        print(f"{kind} val: {right}/{len(va[1])} right")
        for i in np.nonzero(pred != va[1])[0][:15]:
            print(f"  {val_rows[i]['file']}: {classes[pred[i]]}, labeled {classes[va[1][i]]}")
    export(model, out / kind, tr[0].shape[1:3], classes)


def export(model, path, hw, classes):
    model.eval()
    path.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(model, (torch.zeros(1, 3, *hw),), str(path.with_suffix(".onnx")), input_names=["image"], output_names=["class"],
                      dynamic_axes={"image": {0: "n"}, "class": {0: "n"}}, opset_version=17, dynamo=False)
    path.with_suffix(".json").write_text(json.dumps({"classes": classes, "input": {"image": [3, *hw], "scale": "RGB 0..1"},
                                                    "trained": time.strftime("%Y-%m-%d %H:%M")}, indent=2))
    print(f"exported {path.with_suffix('.onnx')}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("data", type=Path)
    ap.add_argument("--out", type=Path, default=Path("models/slots"))
    ap.add_argument("--epochs", type=int, default=40)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--threads", type=int, default=4)
    ap.add_argument("--icons", type=Path, default=Path("models/instruments"))
    ap.add_argument("--holdout", nargs="*", default=[], help="instruments whose real crops are kept out of training")
    ap.add_argument("--only", choices=["chromatic", "instrument"])
    a = ap.parse_args()
    torch.set_num_threads(a.threads)
    if a.only != "instrument":
        # dataset/chromatic_labels.json: checked slot by slot (scan.js took the red mesh behind empty slots for
        # Chromatics, and missed portraits on a dimmed screen).
        checked = a.data.parent / "chromatic_labels.json"
        # "hidden": a slot a tooltip covers (the panel's own help text pops up over it).
        train(a.data, a.out, "chromatic", lambda rows: ["empty", "filled", "hidden"], a.epochs, a.seed,
              json.loads(checked.read_text()) if checked.exists() else None)
    # Instruments: "none" first, then every id named. Names come from dataset/instrument_labels.json when it exists
    # (the scanner's own instrument reads were often wrong), else from trusted frames' labels.
    # With the game's icons (models/instruments), every instrument is a class, held yet or not.
    named = a.data.parent / "instrument_labels.json"
    names = json.loads(named.read_text()) if named.exists() else None
    icons = a.icons if (a.icons / "coin.png").exists() else None
    known = sorted(p.stem for p in a.icons.glob("*.png")) if icons else []
    if a.only != "chromatic":
        # "hidden" last: a slot a tooltip covers, learnt from drawn tooltips and the slots named so.
        train(a.data, a.out, "instrument", lambda rows: ["none"] + sorted(({r["label"] for r in rows} | set(known)) - {"none", "hidden"}) + ["hidden"],
              a.epochs, a.seed, names, icons, set(a.holdout))


if __name__ == "__main__":
    main()
