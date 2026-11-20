#!/usr/bin/env bash
# Usage: view_load.sh TAG X Y SECONDS
# Clicks the point (X, Y), in screen points, and records a screenshot loop for SECONDS. Point it at a rail row, after
# the app has been idle long enough to have settled. Read the result with view_report.py OUT_ROOT/view/TAG.
set -euo pipefail
source "$(dirname "$0")/common.sh"
tag=${1:?tag}; x=${2:?x}; y=${3:?y}; secs=${4:-15}
dir="$OUT_ROOT/view/$tag"; rm -rf "$dir"; mkdir -p "$dir"
pid=$(app_pid); cpu_before=$(cpu_seconds "$pid")
t0=$(now); echo "$t0" > "$dir/t0"
"$HERE/click" "$x" "$y"
shoot_loop "$dir/frames" "$secs"
echo "$cpu_before $(cpu_seconds "$pid")" > "$dir/cpu"
