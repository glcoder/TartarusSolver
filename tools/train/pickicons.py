"""Torture Instrument icons as the pick screen shows them, for telling the cards apart in any language.

From English pick screens whose cards' names were read (dataset/picks): where the icon sits on a card (fitted with
the game's RelicIcon_NN, NN = the instrument's place in TortureInstruments.All), then each instrument's template:
the mean of its icons as seen on screen, or, for one never seen, the game's icon laid over the cards' usual
background. Written as models/instruments/pick/<id>.png (Size x Size, a pixel per Unit canvas units) with pick.json
(the placement, canvas units from the screen's middle as ScreenLayout counts them). Then how well the templates tell
the cards apart, the way PickIcons does it.

    .venv/Scripts/python.exe tools/train/pickicons.py [--art game_art] [--picks dataset/picks]
"""
import argparse, glob, json, os, re
import cv2
import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PITCH = 400  # ScreenLayout.PickPitch
# Where to look for the icon on a card when fitting: canvas units from the screen's middle.
BOX_X, BOX_Y0, BOX_Y1 = 120, -300, -60
SIZE, UNIT, SEARCH = 40, 4, 4  # template pixels, canvas units per pixel, search each way (pixels)


def instrument_ids():
    src = open(os.path.join(ROOT, "src", "Tartarus.Vision", "TortureInstruments.cs"), encoding="utf-8").read()
    body = src[src.index("All ="):src.index("];", src.index("All ="))]
    return re.findall(r'new\("([a-z]+)"', body)


def load_icon(art, n):
    path = glob.glob(os.path.join(art, "**", f"UI_S16Gameplay_RelicIcon_{n:02d}.png"), recursive=True)[0]
    return cv2.imread(path, cv2.IMREAD_UNCHANGED)  # BGRA


def crop(frame, x0, y0, x1, y1, w, h):
    """Canvas box (from the screen's middle) resampled to w x h."""
    fh, fw = frame.shape[:2]
    k = min(fw, fh * 16 / 9) / 1600
    c = frame[int(fh / 2 + y0 * k):int(fh / 2 + y1 * k), int(fw / 2 + x0 * k):int(fw / 2 + x1 * k)]
    return cv2.resize(c, (w, h), interpolation=cv2.INTER_AREA)


def fit(frame, cx, icon):
    reg = crop(frame, cx - BOX_X, BOX_Y0, cx + BOX_X, BOX_Y1, 2 * BOX_X, BOX_Y1 - BOX_Y0)
    best = (-1, 0, 0, 0)
    for size in range(120, 181, 4):
        t = cv2.resize(icon, (size, size), interpolation=cv2.INTER_AREA)
        res = cv2.matchTemplate(reg, t[:, :, :3], cv2.TM_CCOEFF_NORMED, mask=(t[:, :, 3] > 128).astype(np.uint8))
        res[~np.isfinite(res)] = -1
        _, score, _, (x, y) = cv2.minMaxLoc(res)
        if score > best[0]:
            best = (score, size, x - BOX_X + size / 2, BOX_Y0 + y + size / 2)
    return best


def patch(frame, cx, place, pad):
    half = SIZE * UNIT / 2 + pad * UNIT
    x = cx + place["dx"]
    return crop(frame, x - half, place["cy"] - half, x + half, place["cy"] + half, SIZE + 2 * pad, SIZE + 2 * pad)


def ncc(p, t):
    return cv2.minMaxLoc(cv2.matchTemplate(p.astype(np.float32), t.astype(np.float32), cv2.TM_CCOEFF_NORMED))[1]


def picks(folder):
    for js in sorted(glob.glob(os.path.join(folder, "*.json"))):
        pick = json.load(open(js, encoding="utf-8")).get("pick")
        if pick and pick.get("kind") == "instrument":
            yield js, cv2.imread(js[:-5] + ".png"), pick["cards"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--art", default=os.path.join(ROOT, "game_art"))
    ap.add_argument("--picks", default=os.path.join(ROOT, "dataset", "picks"))
    ap.add_argument("--out", default=os.path.join(ROOT, "models", "instruments", "pick"))
    a = ap.parse_args()
    ids = instrument_ids()
    icons = {i: load_icon(a.art, n + 1) for n, i in enumerate(ids)}
    screens = list(picks(a.picks))

    # Where the icon sits and how large: the median of good fits.
    fits = [fit(frame, (c["index"] - 1) * PITCH, icons[c["id"]]) for _, frame, cards in screens for c in cards if c.get("id") in icons]
    good = [f for f in fits if f[0] > 0.6]
    size, dx, cy = (float(np.median([f[k] for f in good])) for k in (1, 2, 3))
    place = {"size": round(size), "dx": round(dx, 1), "cy": round(cy, 1), "unit": UNIT, "pixels": SIZE}
    print(f"{len(fits)} cards, {len(good)} fit well: icon {size:.0f} units, centre {dx:+.1f}, {cy:.1f}")

    # Each instrument's icons as seen, aligned on the first and averaged.
    seen = {}
    for _, frame, cards in screens:
        for c in cards:
            if c.get("id") in icons:
                seen.setdefault(c["id"], []).append(patch(frame, (c["index"] - 1) * PITCH, place, SEARCH).astype(np.float32))
    templates = {}
    # One or two sightings may be a card fading in or under a tooltip: those instruments take the game's icon.
    for i, crops in seen.items():
        if len(crops) < 3:
            continue
        mean = crops[0][SEARCH:SEARCH + SIZE, SEARCH:SEARCH + SIZE]
        for _ in range(2):
            acc = np.zeros_like(mean)
            for p in crops:
                res = cv2.matchTemplate(p, mean, cv2.TM_CCOEFF_NORMED)
                _, _, _, (x, y) = cv2.minMaxLoc(res)
                acc += p[y:y + SIZE, x:x + SIZE]
            mean = acc / len(crops)
        templates[i] = mean
    # The cards' background without an icon: the median over every instrument's template.
    background = np.median(np.stack(list(templates.values())), axis=0)
    scale = SIZE / (size / UNIT)  # template pixels per icon pixel of the fitted size... as a ratio of sizes
    for i in ids:
        if i in templates:
            continue
        icon = cv2.resize(icons[i], (round(size / UNIT), round(size / UNIT)), interpolation=cv2.INTER_AREA).astype(np.float32)
        t = background.copy()
        s = icon.shape[0]
        ox = round(SIZE / 2 - s / 2)
        alpha = icon[:, :, 3:4] / 255
        region = t[ox:ox + s, ox:ox + s]
        t[ox:ox + s, ox:ox + s] = region * (1 - alpha) + icon[:, :, :3] * alpha
        templates[i] = t
        print(f"  {i}: seen {len(seen.get(i, []))} times on pick screens, the game's icon over the cards' background")

    os.makedirs(a.out, exist_ok=True)
    for i, t in templates.items():
        cv2.imwrite(os.path.join(a.out, f"{i}.png"), np.clip(t, 0, 255).astype(np.uint8))
    json.dump(place, open(os.path.join(a.out, "pick.json"), "w"), indent=2)

    right = total = 0
    worst = 1.0
    for js, frame, cards in screens:
        for c in cards:
            if c.get("id") not in icons:
                continue
            p = patch(frame, (c["index"] - 1) * PITCH, place, SEARCH)
            scores = sorted(((ncc(p, t), i) for i, t in templates.items()), reverse=True)
            total += 1
            if scores[0][1] == c["id"]:
                right += 1
                worst = min(worst, scores[0][0])
            else:
                print(f"  {os.path.basename(js)} card {c['index']}: {c['id']} read as {scores[0][1]} ({scores[0][0]:.2f})")
    print(f"{right}/{total} cards told right; lowest right score {worst:.2f}")


if __name__ == "__main__":
    main()
