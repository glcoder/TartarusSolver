#!/usr/bin/env bash
# The value network's improvement loop (AlphaZero-style), unattended:
#   1. the search with the current network plays new runs (tartarus-sim selfplay),
#   2. a candidate network trains on the newest generations of runs (tools/train/valuenet.py),
#   3. candidate and current play the same seeds (tartarus-sim compare); the candidate replaces the current only
#      when it plays better (models/solver/value.bin; each kept network also as value-genN.bin).
# Stops after GENERATIONS, or when dataset/selfplay/STOP exists. Log: dataset/selfplay/loop.log.
#
#   bash tools/train/loop.sh [first generation] [generations]
set -u
cd "$(dirname "$0")/../.."
SIM=tools/Tartarus.SimCli/bin/x64/Release/net9.0/win-x64/tartarus-sim.exe
PY=.venv/Scripts/python.exe
DATA=dataset/selfplay
LOG=$DATA/loop.log
RUNS=${RUNS:-20000}
POLICY=${POLICY:-vn:64:3}
gen=${1:-2}
last=$(( gen + ${2:-100} - 1 ))
mkdir -p "$DATA"
log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*" | tee -a "$LOG"; }

log "loop: generations $gen..$last, $RUNS runs each, policy $POLICY"
while [ "$gen" -le "$last" ] && [ ! -f "$DATA/STOP" ]; do
    log "gen $gen: self-play"
    "$SIM" selfplay --runs "$RUNS" --policy "$POLICY" --seed $(( gen * 7919 )) --out "$DATA/values-$gen.bin" > "$DATA/selfplay-$gen.txt" 2>&1 || { log "gen $gen: self-play failed"; break; }
    log "gen $gen: $(tail -1 "$DATA/selfplay-$gen.txt")"

    files=$(ls "$DATA"/values-*.bin | sort -V | tail -4)
    log "gen $gen: training on $(echo $files | wc -w) generations"
    "$PY" tools/train/valuenet.py $files --out models/solver/candidate.bin --epochs 12 > "$DATA/train-$gen.txt" 2>&1 || { log "gen $gen: training failed"; break; }
    log "gen $gen: $(grep 'epoch 12' "$DATA/train-$gen.txt")"

    "$SIM" compare --runs 800 --seed $(( gen * 31 )) --a "$POLICY" --b "$POLICY" --net-a models/solver/value.bin --net-b models/solver/candidate.bin > "$DATA/compare-$gen.txt" 2>&1 || { log "gen $gen: compare failed"; break; }
    log "gen $gen: $(grep -E '^b - a' "$DATA/compare-$gen.txt") | $(grep -E '^a ' "$DATA/compare-$gen.txt") | $(grep -E '^b ' "$DATA/compare-$gen.txt")"
    if grep -q "better: b" "$DATA/compare-$gen.txt"; then
        cp models/solver/candidate.bin models/solver/value.bin
        cp models/solver/candidate.bin "models/solver/value-gen$gen.bin"
        log "gen $gen: candidate kept"
    else
        log "gen $gen: candidate dropped"
    fi
    gen=$(( gen + 1 ))
done
log "loop: done"
