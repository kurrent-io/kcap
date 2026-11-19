#!/usr/bin/env bash
# Usage: capture_open.sh TAG X Y SECONDS [PORT]
# view_load.sh under a loopback capture of the server port, then one line per HTTP request the app made: method,
# path, status, Content-Encoding and body bytes. Needs passwordless sudo for tcpdump.
set -euo pipefail
source "$(dirname "$0")/common.sh"
tag=${1:?tag}; x=${2:?x}; y=${3:?y}; secs=${4:-15}; port=${5:-9473}
dir="$OUT_ROOT/capture/$tag"; rm -rf "$dir"; mkdir -p "$dir"
sudo -n tcpdump -i lo0 -s 0 -U -w "$dir/open.pcap" "tcp port $port" >/dev/null 2>&1 &
capture=$!
stop_capture() {
  sudo -n pkill -INT -f "tcpdump -i lo0 -s 0 -U -w $dir/open.pcap" || true
  wait "$capture" 2>/dev/null || true
}
trap stop_capture EXIT
sleep 1
"$HERE/view_load.sh" "$tag" "$x" "$y" "$secs"
stop_capture; trap - EXIT
python3 "$HERE/pcap_http.py" "$dir/open.pcap" "$port" | tee "$dir/requests.txt"
