# Sourced by the app-perf scripts. Runs in the guest, in the console user's session.
APP=${KCAP_PERF_APP:-/Applications/kcap.app}
BIN="$APP/Contents/MacOS/kcap-desktop"
OUT_ROOT=${KCAP_PERF_OUT:-$HOME/app-perf-out}
LIFECYCLE=${KCAP_PERF_LIFECYCLE:-$HOME/.config/kcap/app-lifecycle.jsonl}
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

app_pid() { pgrep -f "$BIN" | head -1; }

now() { python3 -c 'import time; print(f"{time.time():.3f}")'; }

quit_app() {
  osascript -e 'tell application id "io.kurrent.kcap" to quit' >/dev/null 2>&1 || true
  for _ in $(seq 1 50); do [ -z "$(app_pid)" ] && return 0; sleep 0.2; done
  pkill -KILL -f "$BIN" || true
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

# shoot_loop DIR SECONDS -> numbered JPEGs named by capture start time, until SECONDS have passed.
shoot_loop() {
  local dir=$1 secs=$2 end
  end=$(python3 -c "import time; print(time.time()+$secs)")
  mkdir -p "$dir"
  while python3 -c "import sys,time; sys.exit(0 if time.time()<$end else 1)"; do
    local t; t=$(now)
    screencapture -x -t jpg "$dir/$t.jpg"
  done
}
