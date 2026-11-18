#!/usr/bin/env bash
# Usage: coldstart.sh TAG RUNS SECONDS
# Quits the app, launches it RUNS times and records each launch for SECONDS: a screenshot loop, the app's CPU seconds
# at 10 s, and the app-lifecycle.jsonl lines it wrote. Read the result with coldstart_report.py OUT_ROOT/coldstart/TAG.
# Discard the first launch after a binary swap: it finishes an update and holds the main window on purpose.
# With KCAP_PERF_FRAMES=0 there is no screenshot loop: a build that writes window_shown and first_sessions_painted is
# timed by its own marks, without the loop taking CPU from it.
set -euo pipefail
source "$(dirname "$0")/common.sh"
tag=${1:?tag}; runs=${2:-6}; secs=${3:-22}
out="$OUT_ROOT/coldstart/$tag"; rm -rf "$out"; mkdir -p "$out"
for run in $(seq 1 "$runs"); do
  one_launch "$out/run$run" "$secs"
  echo "run $run done"
done
