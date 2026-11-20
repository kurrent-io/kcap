#!/usr/bin/env bash
# Usage: idle_cpu.sh LABEL SECONDS
# The app's CPU share and idle wakeups per second over one SECONDS window, and its memory footprint before and after.
# Nothing else should be running; do not touch the guest while it samples.
set -euo pipefail
source "$(dirname "$0")/common.sh"
label=${1:?label}; secs=${2:-60}
pid=$(app_pid)
dir="$OUT_ROOT/idle"; mkdir -p "$dir"
fp() { footprint -p "$pid" 2>/dev/null | awk '/phys_footprint:/ {print $2, $3; exit}'; }
before=$(fp)
cpu0=$(cpu_seconds "$pid")
# idlew is a running count, so the rate is the difference between top's two samples; its second cpu column covers
# exactly the interval.
samples=$(top -l 2 -s "$secs" -pid "$pid" -stats pid,cpu,idlew | awk -v p="$pid" '$1==p {print $2, $3}' | tr '\n' ' ')
cpu1=$(cpu_seconds "$pid")
after=$(fp)
python3 - "$label" "$secs" "$cpu0" "$cpu1" "$samples" "$before" "$after" <<'PY' | tee -a "$dir/idle.jsonl"
import json, sys
label, secs, c0, c1, samples, before, after = sys.argv[1:]
cols = [float(v.rstrip("+")) for v in samples.split()]
idlew = cols[3] - cols[1] if len(cols) == 4 else None
print(json.dumps({"label": label, "seconds": int(secs),
                  "cpu_pct_of_core": round((float(c1) - float(c0)) / int(secs) * 100, 2),
                  "top_cpu_pct": cols[2] if len(cols) == 4 else None,
                  "wakeups_per_s": round(idlew / int(secs), 1) if idlew is not None else None,
                  "footprint_before": before, "footprint_after": after}))
PY
