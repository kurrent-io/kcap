# Sourced by the app-perf scripts. Runs in the guest, in the console user's session.
APP=${KCAP_PERF_APP:-/Applications/kcap.app}
OUT_ROOT=${KCAP_PERF_OUT:-$HOME/app-perf-out}
LIFECYCLE=${KCAP_PERF_LIFECYCLE:-$HOME/.config/kcap/app-lifecycle.jsonl}
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

app_pid() { pgrep -f "/Contents/MacOS/kcap-desktop" | head -1; }

now() { python3 -c 'import time; print(f"{time.time():.3f}")'; }

quit_app() {
  osascript -e 'tell application id "io.kurrent.kcap" to quit' >/dev/null 2>&1 || true
  for _ in $(seq 1 50); do [ -z "$(app_pid)" ] && return 0; sleep 0.2; done
  pkill -KILL -f "/Contents/MacOS/kcap-desktop" || true
  sleep 1
}

# The app must not update itself mid-measurement; an unroutable feed makes every check fail fast.
launch_app() {
  launchctl setenv KCAP_APP_UPDATE_URL http://127.0.0.1:9/
  open "$APP"
}

# cpu_seconds PID -> accumulated user+system CPU of the process, in seconds.
cpu_seconds() {
  ps -o time= -p "$1" 2>/dev/null | awk -F'[:.]' '{ if (NF==3) print $1*60+$2+$3/100; else print $1*3600+$2*60+$3+$4/100 }'
}

# shoot_loop DIR SECONDS -> JPEGs named by a time taken before the capture began, and DIR/times with each one's name
# and the time its capture returned, until SECONDS have passed.
shoot_loop() {
  local dir=$1 secs=$2 end t done_at
  end=$(python3 -c "import time; print(time.time()+$secs)")
  mkdir -p "$dir"
  t=$(now)
  while python3 -c "import sys,time; sys.exit(0 if time.time()<$end else 1)"; do
    screencapture -x -t jpg "$dir/$t.jpg"
    done_at=$(now); echo "$t $done_at" >> "$dir/times"; t=$done_at
  done
}

# stop_shooter: stops the screenshot loop one_launch started, for a caller's exit trap.
SHOOTER=
stop_shooter() {
  [ -n "$SHOOTER" ] || return 0
  pkill -P "$SHOOTER" 2>/dev/null || true
  kill "$SHOOTER" 2>/dev/null || true
  wait "$SHOOTER" 2>/dev/null || true
  SHOOTER=
}

# one_launch DIR SECONDS: quit, launch $APP and record one run into DIR (t0, frames, cpu10, lifecycle.jsonl).
one_launch() {
  local dir=$1 secs=$2 lines_before t0 pid
  quit_app; sleep 5
  mkdir -p "$dir"
  lines_before=$( [ -f "$LIFECYCLE" ] && wc -l < "$LIFECYCLE" || echo 0)
  t0=$(now); echo "$t0" > "$dir/t0"
  launch_app
  if [ "${KCAP_PERF_FRAMES:-1}" = 0 ]; then
    sleep "$secs" & SHOOTER=$!
  else
    shoot_loop "$dir/frames" "$secs" & SHOOTER=$!
  fi
  python3 -c "import time; time.sleep(max(0, $t0 + 10 - time.time()))"
  pid=$(app_pid || true); echo "${pid:-}" > "$dir/pid"
  if [ -n "$pid" ]; then cpu_seconds "$pid" > "$dir/cpu10"; else echo "" > "$dir/cpu10"; fi
  wait "$SHOOTER"; SHOOTER=
  if [ -f "$LIFECYCLE" ]; then tail -n +"$((lines_before + 1))" "$LIFECYCLE" > "$dir/lifecycle.jsonl"; fi
}
