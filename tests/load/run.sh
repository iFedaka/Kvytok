#!/usr/bin/env bash
# Runs the hot-seat burst three times at 200 contenders (the headline), then a ladder of
# rising contention to find where the losing path stops being fast. Each run is a fresh
# event. Records the Api's replica count around each run, since Container Apps decides
# how many replicas take the burst.
#
#   tests/load/run.sh                      # live API, results in tests/load/results
#   LEVELS="50 100 200" tests/load/run.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
results="${RESULTS_DIR:-$here/results}"
levels="${LEVELS:-50 100 200 400 800}"
repeats="${REPEATS:-3}"
rg="${RESOURCE_GROUP:-rg-kvytok-dev}"
mkdir -p "$results"

replicas() {
    command -v az >/dev/null && az containerapp replica list -n api -g "$rg" --query 'length(@)' -o tsv 2>/dev/null || echo '?'
}

run() {
    local contenders=$1 name=$2
    local before after
    mkdir -p "$results/$name"
    before=$(replicas)
    k6 run -q -e CONTENDERS="$contenders" -e RESULTS_DIR="$results/$name" "$here/hot-seat.js"
    after=$(replicas)
    echo "replicas before: $before, after: $after" | tee "$results/$name/replicas.txt"
}

for i in $(seq 1 "$repeats"); do
    run 200 "200-run$i"
done
for level in $levels; do
    run "$level" "ladder-$level"
done
