"""The value network: how a run ends, from a morning's state (tartarus-sim selfplay writes the data).

    .venv/Scripts/python tools/train/valuenet.py dataset/selfplay/values-*.bin [--out models/solver/value.bin] [--epochs 12]

Input: Tartarus.Solver.Features (cells clockwise from the Warden, then the run's globals). Targets: the run's final
utility, Chromatic, Ultimate and Legendary counts. A multilayer perceptron with ReLU, exported for
Tartarus.Solver.ValueNet: "TVNT", layer count, then per layer outputs, inputs, weights (row by row), biases, float32.
"""

import argparse
import glob
import struct
import time
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn

TARGETS = 6  # utility, Chromatic, Ultimate, Legendary, remaining days, the search's value of the morning
SEARCH = 5
HEADS = 4    # the network predicts the first four
HEAD_WEIGHTS = [1.0, 0.3, 0.1, 0.05]


def load(paths, dtype=np.float32):
    """Features as dtype (float16 halves the memory of many generations: they are small counts and fractions),
    targets as float32 (TARGETS columns; the search's value is NaN in files written before it was). Filled file by
    file into arrays sized up front, so the data is never held twice."""
    heads = []
    for p in paths:
        with open(p, "rb") as f:
            magic, features = struct.unpack("<ii", f.read(8))
            if magic == 0x4C415654:  # "TVAL": 5 targets
                (records,), targets, header = struct.unpack("<i", f.read(4)), 5, 12
            elif magic == 0x32415654:  # "TVA2": its target count, then the records
                targets, records = struct.unpack("<ii", f.read(8))
                header = 16
            else:
                raise AssertionError(f"{p}: not a TVAL/TVA2 file")
        heads.append((features, records, targets, header))
    features, total = heads[0][0], sum(h[1] for h in heads)
    x = np.empty((total, features), dtype=dtype)
    y = np.full((total, TARGETS), np.nan, dtype=np.float32)
    at = 0
    for p, (f_count, records, targets, header) in zip(paths, heads):
        assert f_count == features, f"{p}: {f_count} features, not {features}"
        with open(p, "rb") as f:
            f.seek(header)
            data = np.fromfile(f, dtype=np.float32, count=records * (features + targets)).reshape(records, features + targets)
        x[at:at + records] = data[:, :features]
        y[at:at + records, :min(targets, TARGETS)] = data[:, features:features + min(targets, TARGETS)]
        at += records
        print(f"{p}: {records} mornings, {features} features, {targets} targets")
    return x, y, features


class ValueNet(nn.Module):
    def __init__(self, features, hidden=(256, 128)):
        super().__init__()
        layers, n = [], features
        for h in hidden:
            layers += [nn.Linear(n, h), nn.ReLU()]
            n = h
        layers.append(nn.Linear(n, HEADS))
        self.net = nn.Sequential(*layers)

    def forward(self, x):
        return self.net(x)


def export(model, path):
    linears = [m for m in model.net if isinstance(m, nn.Linear)]
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "wb") as f:
        f.write(struct.pack("<ii", 0x544E5654, len(linears)))
        for lin in linears:
            w = lin.weight.detach().cpu().numpy().astype(np.float32)
            b = lin.bias.detach().cpu().numpy().astype(np.float32)
            f.write(struct.pack("<ii", w.shape[0], w.shape[1]))
            f.write(w.tobytes())
            f.write(b.tobytes())
    print(f"exported {path}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("data", nargs="+")
    ap.add_argument("--out", type=Path, default=Path("models/solver/value.bin"))
    ap.add_argument("--epochs", type=int, default=12)
    ap.add_argument("--threads", type=int, default=8)
    ap.add_argument("--hidden", default="256,128", help="hidden layer widths")
    ap.add_argument("--batch", type=int, default=1024)
    ap.add_argument("--lr", type=float, default=1e-3)
    ap.add_argument("--half", action="store_true", help="keep the features as float16 (for many generations)")
    ap.add_argument("--target", default="outcome",
                    help="the utility head's target: outcome (how the run ended), search (the search's value of the "
                         "morning, where there is one), or mix:L (L of the search's, the rest the outcome's)")
    a = ap.parse_args()
    torch.set_num_threads(a.threads)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"device: {torch.cuda.get_device_name(0) if device.type == 'cuda' else 'cpu'}")
    paths = [p for pattern in a.data for p in sorted(glob.glob(pattern))]
    x, y, features = load(paths, np.float16 if a.half else np.float32)
    # The utility target: the search's value is far less noisy than one run's outcome (a morning's search averages
    # many playouts), though a little high (the best of a few noisy means).
    search = y[:, SEARCH]
    known = np.isfinite(search)
    outcome = y[:, 0].copy()  # validation stays against the outcome, whatever the training target
    print(f"target: {a.target} ({known.mean():.1%} of mornings have a search value"
          + (f", mean {search[known].mean():.3f} against outcome mean {outcome[known].mean():.3f}" if known.any() else "") + ")")
    if a.target != "outcome":
        share = 1.0 if a.target == "search" else float(a.target.split(":")[1])
        y[known, 0] = share * search[known] + (1 - share) * y[known, 0]
    # Records come run by run: whole blocks go to validation, so mornings of one run don't straddle the split.
    rng = np.random.default_rng(1)
    blocks = np.arange(len(x)) // 20
    val = np.isin(blocks, rng.choice(blocks.max() + 1, size=max(1, (blocks.max() + 1) // 20), replace=False))
    xt, yt = torch.from_numpy(x[~val]).to(device), torch.from_numpy(y[~val, :HEADS]).to(device)
    yv_np = y[val, :HEADS].copy()
    yv_np[:, 0] = outcome[val]
    xv, yv = torch.from_numpy(x[val]).to(device), torch.from_numpy(yv_np).to(device)
    print(f"{len(xt)} train, {len(xv)} val; utility mean {yt[:, 0].mean():.3f}, sd {yt[:, 0].std():.3f}")
    model = ValueNet(features, tuple(int(h) for h in a.hidden.split(","))).to(device)
    print(f"{sum(p.numel() for p in model.parameters()):,} parameters")
    opt = torch.optim.AdamW(model.parameters(), lr=a.lr, weight_decay=1e-5)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=a.epochs)
    hw = torch.tensor(HEAD_WEIGHTS, device=device)
    for epoch in range(a.epochs):
        model.train()
        t0, total, n = time.time(), 0.0, 0
        perm = torch.randperm(len(xt), device=device)
        for i in range(0, len(xt), a.batch):
            idx = perm[i:i + a.batch]
            loss = (((model(xt[idx].float()) - yt[idx]) ** 2) * hw).mean()
            opt.zero_grad()
            loss.backward()
            opt.step()
            total += loss.item() * len(idx)
            n += len(idx)
        sched.step()
        model.eval()
        with torch.no_grad():
            pv = torch.cat([model(xv[i:i + 65536].float()) for i in range(0, len(xv), 65536)])
        rmse = ((pv[:, 0] - yv[:, 0]) ** 2).mean().sqrt().item()
        corr = np.corrcoef(pv[:, 0].cpu().numpy(), yv[:, 0].cpu().numpy())[0, 1]
        print(f"epoch {epoch + 1}: loss {total / n:.4f}, val utility RMSE {rmse:.3f} (sd {yv[:, 0].std():.3f}), r {corr:.3f} ({time.time() - t0:.1f}s)")
    export(model, a.out)


if __name__ == "__main__":
    main()
