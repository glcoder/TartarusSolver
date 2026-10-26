#!/usr/bin/env bash
# Which target teaches the value network best: self-play that records the search's value of each morning, then three
# networks on the same data — the run's outcome, the search's value, half of each — each against the current one on
# 4000 paired runs. Nothing replaces models/solver/value.bin. Log: dataset/selfplay-v3/targets.log.
#
#   bash tools/train/targets.sh [generations]
set -u
cd "$(dirname "$0")/../.."
SIM=tools/Tartarus.SimCli/bin/x64/Release/net9.0/win-x64/tartarus-sim.exe
PY=.venv/Scripts/python.exe
DATA=dataset/selfplay-v3
LOG=$DATA/targets.log
GENS=${1:-10}
POLICY=${POLICY:-vn:64:3}
mkdir -p "$DATA"
log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*" | tee -a "$LOG"; }

log "targets: $GENS generations, policy $POLICY"
for ((g = 1; g <= GENS; g++)); do
    [ -f "$DATA/STOP" ] && exit 0
    [ -f "$DATA/values-$g.bin" ] && continue
    "$SIM" selfplay --runs 20000 --policy "$POLICY" --seed $((g * 7351 + 3)) --out "$DATA/values-$g.bin" > "$DATA/selfplay-$g.txt" 2>&1 || { log "gen $g failed"; exit 1; }
    log "gen $g: $(tail -1 "$DATA/selfplay-$g.txt")"
done
files=$(ls "$DATA"/values-*.bin | sort -V)
for target in outcome search mix:0.5; do
    [ -f "$DATA/STOP" ] && exit 0
    name=${target/:/-}
    "$PY" -u tools/train/valuenet.py $files --half --epochs 6 --batch 4096 --lr 2e-3 --target "$target" --out "$DATA/net-$name.bin" > "$DATA/train-$name.txt" 2>&1 || { log "$target: training failed"; continue; }
    log "$target: $(grep 'target:' "$DATA/train-$name.txt") | $(grep 'epoch 6' "$DATA/train-$name.txt")"
    "$SIM" compare --runs 4000 --seed 515151 --a "$POLICY" --b "$POLICY" --net-a models/solver/value.bin --net-b "$DATA/net-$name.bin" > "$DATA/compare-$name.txt" 2>&1
    log "$target: $(grep -E '^b - a' "$DATA/compare-$name.txt") | $(grep -E '^b ' "$DATA/compare-$name.txt") | $(grep better "$DATA/compare-$name.txt")"
done
log "targets: done"
