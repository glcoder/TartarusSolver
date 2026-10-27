"""The playouts' policy network: which action the search would take, from each action's after-state
(tartarus-sim policydata writes the data).

    .venv/Scripts/python tools/train/policynet.py dataset/policy/actions-*.bin [--out models/solver/policy.bin] [--hidden 32]

Input: Tartarus.Solver.Features of the state after the action (the same dice for all of a decision's actions). The
network scores each after-state; over a decision's actions the scores' softmax is fitted to the softmax of the
search's means over a temperature (in Chromatic units). Instrument picks are left out (the playouts take those by
the measured instrument values) unless --instruments: then the network takes them too (the search's "pi").
Reported per decision kind: how often the network's choice is the search's, and its regret (the search's best mean
minus the mean of the action taken), against the heuristic policy's.
Exported for Tartarus.Solver.ValueNet with one output: "TVNT", layer count, then per layer outputs, inputs, weights
(row by row), biases, float32.
"""

import argparse
import glob
import struct
import time
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn

from valuenet import export

KINDS = ["PickInstrument", "Reroll", "PickTreasure", "Interrogate", "Chain", "StopChain", "Ratway", "Move",
         "Consume", "Replace", "Hunt", "End"]
LEFT_OUT = {0, 1}  # instrument picks and their Re-roll


def row_type(features):
    return np.dtype([("x", "<f2", (features,)), ("mean", "<f4"), ("se", "<f4"), ("playouts", "<i4"),
                     ("decision", "<i4"), ("kind", "u1"), ("flags", "u1"), ("day", "u1"), ("pad", "u1")])


def load(paths, instruments=False):
    """All rows, decisions numbered across the files; decisions that are instrument picks dropped unless instruments."""
    parts, offset = [], 0
    for p in paths:
        with open(p, "rb") as f:
            magic, features, rows = struct.unpack("<iii", f.read(12))
            assert magic == 0x31445054, f"{p}: not a TPD1 file"
            data = np.fromfile(f, dtype=row_type(features), count=rows)
        data["decision"] += offset
        offset = int(data["decision"].max()) + 1
        print(f"{p}: {rows} actions, {len(np.unique(data['decision']))} decisions")
        parts.append(data)
    data = np.concatenate(parts)
    # A decision's kind: its first action's (a decision mixes kinds: Consume, Replace, End are one choice).
    first = np.r_[True, data["decision"][1:] != data["decision"][:-1]]
    group = np.cumsum(first) - 1
    has_pick = np.zeros(group[-1] + 1, bool)
    np.logical_or.at(has_pick, group, np.isin(data["kind"], list(LEFT_OUT)))
    keep = np.ones(len(group), bool) if instruments else ~has_pick[group]
    data = data[keep]
    first = np.r_[True, data["decision"][1:] != data["decision"][:-1]]
    group = np.cumsum(first) - 1
    kind = np.zeros(group[-1] + 1, np.int64)
    # The decision's kind for the report: Move, Consume (the day's card), Chain..., by its most telling action.
    for k in ["Move", "Ratway", "Chain", "Interrogate", "PickTreasure", "Hunt", "Consume", "Replace", "End", "StopChain"]:
        has = np.zeros(group[-1] + 1, bool)
        np.logical_or.at(has, group, data["kind"] == KINDS.index(k))
        kind[(kind == 0) & has] = KINDS.index(k)
    print(f"{len(data)} actions in {group[-1] + 1} decisions" + ("" if instruments else " (instrument picks left out)"))
    return data, group, kind


class PolicyNet(nn.Module):
    def __init__(self, features, hidden):
        super().__init__()
        layers, n = [], features
        for h in hidden:
            layers += [nn.Linear(n, h), nn.ReLU()]
            n = h
        layers.append(nn.Linear(n, 1))
        self.net = nn.Sequential(*layers)

    def forward(self, x):
        return self.net(x).squeeze(-1)


def group_log_softmax(v, g, groups):
    """log softmax of v within each group g (0..groups-1)."""
    m = torch.full((groups,), -1e30, device=v.device).scatter_reduce(0, g, v, "amax")
    e = torch.exp(v - m[g])
    s = torch.zeros(groups, device=v.device).index_add(0, g, e)
    return v - m[g] - torch.log(s[g])


def group_argmax(v, g, groups):
    """Per group, the index (into v) of its largest value (the first of ties)."""
    m = torch.full((groups,), -1e30, device=v.device).scatter_reduce(0, g, v, "amax")
    idx = torch.arange(len(v), device=v.device)
    cand = torch.where(v >= m[g], idx, torch.full_like(idx, len(v)))
    return torch.full((groups,), len(v), device=v.device).scatter_reduce(0, g, cand, "amin")


def report(name, chosen, mean, g, groups, best_mean, kinds, kind_names):
    """Agreement with the search's best and regret, overall and by decision kind."""
    regret = (best_mean - mean[chosen]).cpu().numpy()
    best = (regret <= 1e-6)
    lines = [f"{name}: agrees {best.mean():.1%}, regret {regret.mean():.4f}"]
    for k in np.unique(kinds):
        sel = kinds == k
        lines.append(f"  {kind_names[k]:<12} {sel.sum():>8} decisions: agrees {best[sel].mean():.1%}, regret {regret[sel].mean():.4f} (sum {regret[sel].sum() / len(regret):.4f})")
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("data", nargs="+")
    ap.add_argument("--out", type=Path, default=Path("models/solver/policy.bin"))
    ap.add_argument("--epochs", type=int, default=10)
    ap.add_argument("--hidden", default="32", help="hidden layer widths (empty: linear)")
    ap.add_argument("--batch", type=int, default=4096, help="decisions per batch")
    ap.add_argument("--lr", type=float, default=2e-3)
    ap.add_argument("--tau", type=float, default=0.1, help="temperature of the search's means, Chromatic units")
    ap.add_argument("--instruments", action="store_true", help="keep the instrument picks (and their Re-roll)")
    a = ap.parse_args()
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"device: {torch.cuda.get_device_name(0) if device.type == 'cuda' else 'cpu'}")
    paths = [p for pattern in a.data for p in sorted(glob.glob(pattern))]
    data, group, kind = load(paths, a.instruments)
    groups = int(group[-1]) + 1
    features = data["x"].shape[1]
    x = torch.from_numpy(np.ascontiguousarray(data["x"])).to(device)
    mean = torch.from_numpy(data["mean"].astype(np.float32)).to(device)
    g = torch.from_numpy(group).to(device)
    heuristic = torch.from_numpy((data["flags"] & 1).astype(bool)).to(device)
    target = torch.exp(group_log_softmax(mean / a.tau, g, groups))
    # Decisions by runs' blocks: one in twenty blocks of 64 decisions for validation.
    rng = np.random.default_rng(1)
    blocks = np.arange(groups) // 64
    val_groups = np.isin(blocks, rng.choice(blocks.max() + 1, size=max(1, (blocks.max() + 1) // 20), replace=False))
    starts = np.r_[0, np.flatnonzero(np.diff(group)) + 1]
    sizes = np.diff(np.r_[starts, len(group)])
    starts_t, sizes_t = torch.from_numpy(starts).to(device), torch.from_numpy(sizes).to(device)
    train_ids = torch.from_numpy(np.flatnonzero(~val_groups)).to(device)
    val_ids = np.flatnonzero(val_groups)

    def rows_of(ids):
        n = sizes_t[ids]
        offsets = torch.repeat_interleave(starts_t[ids] - torch.cumsum(n, 0) + n, n)
        return offsets + torch.arange(int(n.sum()), device=device), torch.repeat_interleave(torch.arange(len(ids), device=device), n)

    vrows, vg = rows_of(torch.from_numpy(val_ids).to(device))
    vmean = mean[vrows]
    vbest = torch.full((len(val_ids),), -1e30, device=device).scatter_reduce(0, vg, vmean, "amax")
    vkinds = kind[val_ids]
    # The heuristic's pick: the flagged action (one per decision).
    hpick = torch.full((len(val_ids),), 0, device=device, dtype=torch.long).index_put_((vg[heuristic[vrows]],), torch.nonzero(heuristic[vrows]).squeeze(1))
    print(report("heuristic policy", hpick, vmean, vg, len(val_ids), vbest, vkinds, KINDS))
    hidden = tuple(int(h) for h in a.hidden.split(",") if h)
    model = PolicyNet(features, hidden).to(device)
    print(f"{sum(p.numel() for p in model.parameters()):,} parameters, {len(train_ids)} train / {len(val_ids)} val decisions, tau {a.tau}")
    opt = torch.optim.AdamW(model.parameters(), lr=a.lr, weight_decay=1e-5)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=a.epochs)
    for epoch in range(a.epochs):
        model.train()
        t0, total, n = time.time(), 0.0, 0
        perm = train_ids[torch.randperm(len(train_ids), device=device)]
        for i in range(0, len(perm), a.batch):
            ids = perm[i:i + a.batch]
            rows, bg = rows_of(ids)
            logp = group_log_softmax(model(x[rows].float()), bg, len(ids))
            loss = -(target[rows] * logp).sum() / len(ids)
            opt.zero_grad()
            loss.backward()
            opt.step()
            total += loss.item() * len(ids)
            n += len(ids)
        sched.step()
        model.eval()
        with torch.no_grad():
            v = torch.cat([model(x[vrows[j:j + 262144]].float()) for j in range(0, len(vrows), 262144)])
            pick = group_argmax(v, vg, len(val_ids))
            regret = (vbest - vmean[pick]).mean().item()
            agree = ((vbest - vmean[pick]) <= 1e-6).float().mean().item()
        print(f"epoch {epoch + 1}: loss {total / n:.4f}, val agrees {agree:.1%}, regret {regret:.4f} ({time.time() - t0:.1f}s)")
    print(report("policy network", pick, vmean, vg, len(val_ids), vbest, vkinds, KINDS))
    export(model, a.out)


if __name__ == "__main__":
    main()
