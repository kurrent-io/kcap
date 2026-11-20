#!/usr/bin/env bash
# Usage: capture_open.sh TAG X Y SECONDS [PORT]
# view_load.sh under a loopback capture of the server port, then one line per HTTP request the app made: method,
# path, status, Content-Encoding and body bytes. Needs passwordless sudo for sh, tcpdump and kill.
set -euo pipefail
source "$(dirname "$0")/common.sh"
tag=${1:?tag}; x=${2:?x}; y=${3:?y}; secs=${4:-15}; port=${5:-9473}
dir="$OUT_ROOT/capture/$tag"; rm -rf "$dir"; mkdir -p "$dir"
sudo -n sh -c 'echo $$ > "$1"; exec tcpdump -i lo0 -s 0 -U -w "$2" "tcp port $3"' sh "$dir/tcpdump.pid" "$dir/open.pcap" \
  "$port" >/dev/null 2>&1 &
capture=$!
stop_capture() {
  local tpid
  for _ in $(seq 1 50); do [ -s "$dir/tcpdump.pid" ] && break; sleep 0.1; done
  tpid=$(cat "$dir/tcpdump.pid" 2>/dev/null || true)
  if [ -z "$tpid" ] || ! sudo -n kill -INT "$tpid"; then
    echo "could not stop tcpdump (pid ${tpid:-unknown}); stop it by hand" >&2
    return 1
  fi
  wait "$capture" 2>/dev/null || true
}
trap stop_capture EXIT
sleep 1
"$HERE/view_load.sh" "$tag" "$x" "$y" "$secs"
stop_capture; trap - EXIT
python3 "$HERE/pcap_http.py" "$dir/open.pcap" "$port" | tee "$dir/requests.txt"
