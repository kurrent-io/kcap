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
  quit_app; sleep 5
  dir="$out/run$run"; mkdir -p "$dir"
  lines_before=$( [ -f "$LIFECYCLE" ] && wc -l < "$LIFECYCLE" || echo 0)
  t0=$(now); echo "$t0" > "$dir/t0"
  launch_app
  if [ "${KCAP_PERF_FRAMES:-1}" = 0 ]; then
    sleep "$secs" & shooter=$!
  else
    shoot_loop "$dir/frames" "$secs" & shooter=$!
  fi
  python3 -c "import time; time.sleep(max(0, $t0 + 10 - time.time()))"
  pid=$(app_pid || true); echo "${pid:-}" > "$dir/pid"
  [ -n "$pid" ] && cpu_seconds "$pid" > "$dir/cpu10" || echo "" > "$dir/cpu10"
  wait "$shooter"
  [ -f "$LIFECYCLE" ] && tail -n +"$((lines_before + 1))" "$LIFECYCLE" > "$dir/lifecycle.jsonl" || true
  echo "run $run done"
done
