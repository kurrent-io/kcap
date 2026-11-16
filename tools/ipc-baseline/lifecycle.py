#!/usr/bin/env python3
"""Recovery after a daemon or local-server restart.

Usage:
  lifecycle.py daemon RUNS [--name DAEMON]   restarts the daemon's launch agent and times its socket: the first Hello
                                             answered (a starting daemon counts) and the first Hello from a ready one.
                                             A daemon that never reports `starting` reads ready on its first answer,
                                             so there ready_s equals first_hello_s.
  lifecycle.py server RUNS --label LABEL     restarts the local server's launch agent and times /healthz/ready.
  [--url http://localhost:9473]
Both use `launchctl kickstart -k gui/<uid>/<label>`; the daemon's label is read from `launchctl list`. Prints JSON."""
import argparse
import json
import os
import subprocess
import time
import urllib.request

import kcapipc as k


def kickstart(label):
    subprocess.run(["launchctl", "kickstart", "-k", f"gui/{os.getuid()}/{label}"], check=True)


def daemon_label(name):
    out = subprocess.run(["launchctl", "list"], capture_output=True, text=True).stdout
    labels = [line.split()[-1] for line in out.splitlines() if "kcap.daemon." in line]
    if name:
        labels = [label for label in labels if label.endswith("." + name)]
    if len(labels) != 1:
        raise SystemExit(f"need exactly one kcap daemon launch agent{' named ' + name if name else ''}; found {labels}")
    return labels[0]


def wait_for(pred, timeout, start=None):
    """Seconds from start (default: now) until pred holds, or None."""
    start = time.perf_counter() if start is None else start
    while time.perf_counter() - start < timeout:
        try:
            if pred():
                return time.perf_counter() - start
        except Exception:
            pass
        time.sleep(0.02)
    return None


def daemon_run(path, label):
    try:
        before = k.hello(path)[0] or {}
    except Exception:
        before = {}
    start = time.perf_counter()
    kickstart(label)

    def answered():
        reply, _ = k.hello(path, accepts_starting=True, timeout=0.5)
        return reply is not None and reply.get("pid") != before.get("pid")

    def ready():
        reply, _ = k.hello(path, accepts_starting=True, timeout=0.5)
        return reply is not None and reply.get("pid") != before.get("pid") and not reply.get("starting")

    first = wait_for(answered, 60, start)
    done = wait_for(ready, 120, start)
    return {"first_hello_s": first, "ready_s": done}


def server_run(url, label):
    start = time.perf_counter()
    kickstart(label)
    # The old process can still answer for a moment after the restart request.
    time.sleep(0.5)

    def healthy():
        with urllib.request.urlopen(url + "/healthz/ready", timeout=1) as r:
            return r.status == 200

    return {"ready_s": wait_for(healthy, 300, start)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["daemon", "server"])
    ap.add_argument("runs", type=int)
    ap.add_argument("--name")
    ap.add_argument("--label")
    ap.add_argument("--url", default="http://localhost:9473")
    a = ap.parse_args()
    runs = []
    for _ in range(a.runs):
        if a.mode == "daemon":
            runs.append(daemon_run(k.socket_path(a.name), a.label or daemon_label(a.name)))
        else:
            runs.append(server_run(a.url, a.label))
        time.sleep(3)
    print(json.dumps({"mode": a.mode, "runs": runs}, indent=2))


if __name__ == "__main__":
    main()
