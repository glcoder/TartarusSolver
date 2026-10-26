"""Name the Torture Instruments seen in the dataset's instrument slots, a group of look-alike icons at a time.

    .venv/Scripts/python tools/train/instruments.py dataset [--port 8766] [--threshold 0.9]

Every instrument slot crop (dataset/train/slots/*_instrumentN.png, from `tartarus-scan export`) is grouped with the
crops it looks like: an icon is drawn the same in every frame, so a group is one instrument, an empty slot, or a
slot hidden by an overlay (a Consume pick's red). A local page shows each group; pick its name once. Names are
written to dataset/instrument_labels.json ("<frame id>/<slot>": id), which tools/train/slotnet.py trains on.
"""

import argparse
import csv
import json
import threading
import webbrowser
from collections import Counter
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import numpy as np
from PIL import Image

# The game's instrument names (Torture Instrument cards), by id.
NAMES = {
    "scales": "Warden's Scales", "noose": "Hangman's Noose", "pestle": "Iron Pestle", "brand": "Searing Branding Iron",
    "keys": "Lost Ring of Keys", "hook": "Sorting Hook", "tongs": "Iron Tongs of Lust: Mammoth",
    "skewer": "Skewered Rat Heads: Colossal Rat", "guillotine": "Fanged Guillotine: Alpha Wolf", "casket": "Avenger's Casket",
    "muzzle": "Raging Beast's Agonizing Muzzle", "saw": "Rusty Saw", "ironbox": "Squirming Iron Box: Colossal Rat",
    "axe": "Gaoler's Execution Axe", "shackles": "Long-Chained Shackles", "shortchain": "Short-Chained Shackles",
    "collar": "Maddened Beast's Barbed Collar", "cage": "Crowded Beastcage", "maiden": "Severed Iron Maiden",
    "whip": "Barbed Whip", "stake": "Burning Stake", "coin": "Kin's Coin of Fate", "scapegoat": "Scapegoat's Guillotine",
    "dogtag": "Felon's Dog Tag", "maul": "Elephant-Foot Maul: Mammoth", "claws": "Filthy Claws: Alpha Wolf",
    "trap": "Blood-Stained Trap: Berserk Lion", "crown": "Iron Crown of the Prison King: Berserk Lion", "forged": "Forged Key",
    "chains": "Ferocious Beast's Ravenous Chains", "blacklist": "Executioner's Blacklist",
}
SPECIAL = {"none": "пусто (слот без инструмента)", "hidden": "не видно (красный экран, затемнение, подсказка)"}


def features(path):
    """The icon's outline: blurred gradient magnitude of a small gray picture, unit-length. Outlines ignore the
    slot's background (wood, a Consume pick's red, a fade), which color pictures grouped by instead of the icon."""
    a = np.asarray(Image.open(path).convert("L").resize((32, 52), Image.BOX), dtype=np.float32)
    gx, gy = np.zeros_like(a), np.zeros_like(a)
    gx[:, 1:-1] = a[:, 2:] - a[:, :-2]
    gy[1:-1] = a[2:] - a[:-2]
    g = np.sqrt(gx ** 2 + gy ** 2)[4:-4, 4:-4]
    g = np.lib.stride_tricks.sliding_window_view(np.pad(g, 1, mode="edge"), (3, 3)).mean((-1, -2)).ravel()
    return g / (np.linalg.norm(g) + 1e-6)


def group(vectors, threshold):
    """Leader clustering: each crop joins the most alike group if alike enough, else starts one."""
    centers, members = [], []
    for i, v in enumerate(vectors):
        if centers:
            sims = np.array(centers) @ v
            j = int(sims.argmax())
            if sims[j] >= threshold:
                members[j].append(i)
                c = np.mean([vectors[k] for k in members[j]], 0)
                centers[j] = c / (np.linalg.norm(c) + 1e-6)
                continue
        centers.append(v)
        members.append([i])
    return members


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dataset", type=Path)
    ap.add_argument("--port", type=int, default=8766)
    ap.add_argument("--threshold", type=float, default=0.9)
    a = ap.parse_args()
    train = a.dataset / "train"
    rows = [r for r in csv.DictReader(open(train / "slots.csv", newline="")) if r["kind"] == "instrument"]
    keys = [f"{r['frame'][:-4]}/{r['index']}" for r in rows]
    vectors = [features(train / "slots" / r["file"]) for r in rows]
    out = a.dataset / "instrument_labels.json"
    labels = json.loads(out.read_text()) if out.exists() else {}
    # Groups with crops nobody has named yet first (new frames after more runs), the biggest first.
    groups = sorted(group(vectors, a.threshold), key=lambda g: (all(keys[i] in labels for i in g), -len(g)))
    print(f"{len(rows)} slot crops in {len(groups)} groups; {sum(len(g) >= 3 for g in groups)} of 3 or more")

    def page():
        cards = []
        for gi, g in enumerate(groups):
            named = Counter(labels.get(keys[i]) for i in g).most_common(1)[0][0]
            hint = Counter(rows[i]["label"] for i in g).most_common(2)
            sample = [rows[i]["file"] for i in g[:: max(1, len(g) // 10)]][:10]
            options = "".join(f'<option value="{k}"{" selected" if k == named else ""}>{v}</option>'
                              for k, v in [("", "— выберите —"), *SPECIAL.items(), *sorted(NAMES.items(), key=lambda kv: kv[1])])
            imgs = "".join(f'<img src="/img/{f}">' for f in sample)
            cards.append(f'<div class="g{" done" if named else ""}" id="g{gi}"><div class="h">Группа {gi + 1} · {len(g)} шт. · сканер думал: '
                         f'{", ".join(f"{k} {n}" for k, n in hint)}</div><div class="imgs">{imgs}</div>'
                         f'<select data-g="{gi}">{options}</select></div>')
        return f"""<!doctype html><html lang="ru"><meta charset="utf-8"><title>Torture Instruments</title>
<style>:root{{color-scheme:dark}} body{{background:#1b1c21;color:#ececf0;font:16px "Segoe UI",sans-serif;margin:20px}}
.g{{background:#25262d;border:1px solid #383a44;border-radius:8px;padding:12px;margin:0 0 14px}} .g.done{{border-color:#4caf6a}}
.h{{color:#9aa0ac;margin-bottom:8px}} .imgs img{{width:126px;height:208px;margin:0 6px 6px 0;border-radius:4px}}
select{{font:inherit;padding:6px;background:#30323a;color:#ececf0;border:1px solid #4a4d58;border-radius:6px;min-width:420px}}</style>
<h2>Torture Instruments: {len(groups)} групп</h2><p style="color:#9aa0ac">Выберите, что изображено в каждой группе. Сохраняется сразу.
Если в группе разные иконки — пропустите её.</p>{''.join(cards)}
<script>document.addEventListener('change', async e => {{ const s = e.target; if (!s.dataset.g) return;
await fetch('/name?g=' + s.dataset.g + '&id=' + encodeURIComponent(s.value), {{ method: 'POST' }});
s.closest('.g').classList.toggle('done', !!s.value); }});</script>"""

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_GET(self):
            if self.path == "/":
                body, kind = page().encode(), "text/html; charset=utf-8"
            elif self.path.startswith("/img/"):
                f = train / "slots" / Path(self.path[5:]).name
                if not f.exists():
                    self.send_error(404)
                    return
                body, kind = f.read_bytes(), "image/png"
            else:
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Type", kind)
            self.end_headers()
            self.wfile.write(body)

        def do_POST(self):
            from urllib.parse import parse_qs, urlparse
            q = parse_qs(urlparse(self.path).query)
            g, name = int(q["g"][0]), q.get("id", [""])[0]
            for i in groups[g]:
                if name:
                    labels[keys[i]] = name
                else:
                    labels.pop(keys[i], None)
            out.write_text(json.dumps(labels, indent=1, sort_keys=True))
            print(f"group {g + 1} ({len(groups[g])}): {name or '—'}")
            self.send_response(200)
            self.end_headers()

    server = ThreadingHTTPServer(("localhost", a.port), Handler)
    print(f"instrument page: http://localhost:{a.port}/  (Ctrl+C to stop)")
    server.serve_forever()


if __name__ == "__main__":
    main()
