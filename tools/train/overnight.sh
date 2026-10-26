#!/usr/bin/env bash
# A long unattended retraining of the value network after the rules changed (what the 6-hour loop of 2026-10-24
# taught: train once on all the generations, judge with 4000 paired runs, not 800):
#   phase A: self-play with the current network, then a network trained on all of it, kept if it plays better;
#   phase B: self-play again with the (maybe new) network, then one trained on all of A and B, kept if better;
#   at the end, the hint levels remeasured when the network changed.
# Stops between steps when dataset/selfplay-v2/STOP exists. Log: dataset/selfplay-v2/overnight.log.
#
#   bash tools/train/overnight.sh [generations per phase]
set -u
cd "$(dirname "$0")/../.."
SIM=tools/Tartarus.SimCli/bin/x64/Release/net9.0/win-x64/tartarus-sim.exe
PY=.venv/Scripts/python.exe
DATA=dataset/selfplay-v2
LOG=$DATA/overnight.log
GENS=${1:-24}
RUNS=${RUNS:-20000}
POLICY=${POLICY:-vn:64:3}
mkdir -p "$DATA"
log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*" | tee -a "$LOG"; }
stopped() { [ -f "$DATA/STOP" ]; }
changed=0

selfplay() { # phase, first generation
    local phase=$1 first=$2
    for ((g = first; g < first + GENS; g++)); do
        stopped && return
        [ -f "$DATA/values-$g.bin" ] && continue
        "$SIM" selfplay --runs "$RUNS" --policy "$POLICY" --seed $((g * 104729 + 17)) --out "$DATA/values-$g.bin" > "$DATA/selfplay-$g.txt" 2>&1 \
            || { log "$phase gen $g: self-play failed"; return 1; }
        log "$phase gen $g: $(tail -1 "$DATA/selfplay-$g.txt")"
    done
}

train_and_judge() { # phase
    local phase=$1
    stopped && return
    local files
    files=$(ls "$DATA"/values-*.bin | sort -V)
    log "$phase: training on $(echo $files | wc -w) generations"
    "$PY" -u tools/train/valuenet.py $files --half --epochs 6 --batch 4096 --lr 2e-3 --out "$DATA/candidate-$phase.bin" > "$DATA/train-$phase.txt" 2>&1 \
        || { log "$phase: training failed"; return 1; }
    log "$phase: $(grep 'epoch 6' "$DATA/train-$phase.txt")"
    stopped && return
    "$SIM" compare --runs 4000 --seed $((RANDOM * 7 + 900001)) --a "$POLICY" --b "$POLICY" --net-a models/solver/value.bin --net-b "$DATA/candidate-$phase.bin" > "$DATA/compare-$phase.txt" 2>&1 \
        || { log "$phase: compare failed"; return 1; }
    log "$phase: $(grep -E '^b - a' "$DATA/compare-$phase.txt") | $(grep -E '^a ' "$DATA/compare-$phase.txt") | $(grep -E '^b ' "$DATA/compare-$phase.txt")"
    if grep -q "better: b" "$DATA/compare-$phase.txt"; then
        cp models/solver/value.bin "$DATA/value-before-$phase.bin"
        cp "$DATA/candidate-$phase.bin" models/solver/value.bin
        cp "$DATA/candidate-$phase.bin" "models/solver/value-rules2-$phase.bin"
        changed=1
        log "$phase: candidate kept"
    else
        log "$phase: candidate dropped"
    fi
}

log "overnight: $GENS generations per phase, $RUNS runs each, policy $POLICY"
selfplay A 1 && train_and_judge A
selfplay B $((GENS + 1)) && train_and_judge B
if [ "$changed" = 1 ] && ! stopped; then
    cp models/solver/levels.txt "$DATA/levels-before.txt"
    "$SIM" levels --runs 400 > "$DATA/levels.txt" 2>&1 && log "levels: $(tr '\n' ' ' < models/solver/levels.txt | sed 's/#[^p]*//g')"
fi
log "overnight: done"
