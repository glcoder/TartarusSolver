"""Compare cell networks (ONNX) on the val frames of cellnet.py's split, plain and red apart, listing the errors.

    .venv/Scripts/python tools/train/celleval.py dataset/train dataset/exp/plain dataset/exp/red [--seed 1]
"""
import argparse
import csv
import sys
from pathlib import Path

import numpy as np
import onnxruntime as ort

sys.path.insert(0, str(Path(__file__).parent))
from cellnet import CODES, RARITIES, KINDS, IGNORE, cell_targets, is_red, load_image, one_hot, split_by_frame  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("data", type=Path)
    ap.add_argument("models", type=Path, nargs="+", help="folders with cellnet.onnx")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--since", help="only val frames from this id on (e.g. 20261025_): ones a model trained earlier never saw")
    a = ap.parse_args()
    rows = [r for r in csv.DictReader(open(a.data / "cells.csv", newline="")) if r.get("trusted", "1") == "1"]
    _, val = split_by_frame(rows, 0.15, a.seed)
    if a.since:
        val = [r for r in val if r["frame"] >= a.since]
    images = np.stack([load_image(a.data / "cells" / r["file"]) for r in val]).transpose(0, 3, 1, 2).copy()
    cells = np.stack([one_hot(int(r["cell"])) for r in val])
    targets = [cell_targets(r) for r in val]
    for folder in a.models:
        sess = ort.InferenceSession(str(folder / "cellnet.onnx"), providers=["CPUExecutionProvider"])
        kind, code, rarity = sess.run(["kind", "code", "rarity"], {"image": images, "cell": cells})
        print(f"== {folder}")
        for name, keep in (("plain", lambda r: not is_red(r)), ("red", is_red)):
            idx = [i for i, r in enumerate(val) if keep(r)]
            errs = []
            stats = {"kind": [0, 0], "code": [0, 0], "rarity": [0, 0]}
            for i in idx:
                t = targets[i]
                for h, (logits, want, names) in {"kind": (kind, t[0], KINDS), "code": (code, t[1], CODES), "rarity": (rarity, t[2], RARITIES)}.items():
                    if want == IGNORE:
                        continue
                    got = int(logits[i].argmax())
                    stats[h][1] += 1
                    if got == want:
                        stats[h][0] += 1
                    else:
                        errs.append(f"    {val[i]['file']}: {h} {names[got]} want {names[want]}")
            print(f"  {name}: " + " ".join(f"{h} {ok}/{n}" for h, (ok, n) in stats.items()))
            for e in errs[:15]:
                print(e)


if __name__ == "__main__":
    main()
