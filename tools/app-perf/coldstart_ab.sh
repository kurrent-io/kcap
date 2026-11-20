#!/usr/bin/env bash
# Usage: coldstart_ab.sh RUNS SECONDS TAG=BUNDLE_PATH [TAG=BUNDLE_PATH ...]
# Cold starts several builds in turn, one launch each per round, so load from outside the guest falls on every build
# alike. The app only runs as itself from /Applications (anywhere else it offers to open the installed copy), so each
# launch renames its bundle into place and back out after; keep the bundles on the same volume. Stamp every build with
# the same version. Each build's first launch is a warm-up and is not kept. Read each result with
# coldstart_report.py OUT_ROOT/coldstart/TAG.
set -euo pipefail
source "$(dirname "$0")/common.sh"
runs=${1:?runs}; secs=${2:?seconds}; shift 2
{ [ -e "$APP" ] || [ -L "$APP" ]; } && { echo "move $APP out of the way first" >&2; exit 1; }
# A symlink would run the app from its target, not /Applications, and a move across volumes copies the whole bundle.
apps_dev=$(stat -f %d "$(dirname "$APP")")
for pair in "$@"; do
  bundle=${pair#*=}
  if [ -L "$bundle" ] || [ ! -d "$bundle" ] || [ "$(stat -f %d "$bundle")" != "$apps_dev" ]; then
    echo "$bundle: needs a real bundle directory on the same volume as $(dirname "$APP")" >&2; exit 1
  fi
done

moved=
restore() {
  stop_shooter
  [ -n "$moved" ] && { [ -e "$APP" ] || [ -L "$APP" ]; } || { moved=; return 0; }
  quit_app
  mv "$APP" "$moved"; moved=
}
trap restore EXIT
trap 'exit 130' INT TERM

with_bundle() {
  local bundle=$1; shift
  moved=$bundle; mv "$bundle" "$APP"
  "$@"
  restore
}

for pair in "$@"; do
  tag=${pair%%=*}; rm -rf "$OUT_ROOT/coldstart/$tag" "$OUT_ROOT/coldstart/.warmup-$tag"
  with_bundle "${pair#*=}" one_launch "$OUT_ROOT/coldstart/.warmup-$tag" "$secs"
done
for run in $(seq 1 "$runs"); do
  for pair in "$@"; do
    with_bundle "${pair#*=}" one_launch "$OUT_ROOT/coldstart/${pair%%=*}/run$run" "$secs"
  done
  echo "round $run done"
done
