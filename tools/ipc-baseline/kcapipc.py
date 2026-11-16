"""Shared helpers for the IPC baseline scripts: the daemon's local frame codec, paths and timing.

Frames are [type:1][len:4 BE][payload]; the text frames these scripts use carry UTF-8."""
import json
import os
import socket
import statistics
import struct
import time

HELLO, STATUS_SUBSCRIBE, LIST, CONSENT_RULES_GET, ACCOUNTS_SUBSCRIBE = 15, 16, 6, 13, 32
ERROR, AGENT_LIST, HELLO_REPLY, DAEMON_STATUS, CONSENT_RULES, ACCOUNTS_SNAPSHOT = 67, 68, 75, 76, 73, 87


def daemons_dir():
    return os.environ.get("KCAP_DAEMONS_DIR") or os.path.expanduser("~/.config/kcap/daemons")


def socket_path(name=None):
    if name:
        return os.path.join(daemons_dir(), name + ".sock")
    socks = sorted(f for f in os.listdir(daemons_dir()) if f.endswith(".sock"))
    if not socks:
        raise SystemExit("no daemon socket in " + daemons_dir())
    return os.path.join(daemons_dir(), socks[0])


def connect(path, timeout=5.0):
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.settimeout(timeout)
    s.connect(path)
    return s


def send(s, ftype, text=""):
    payload = text.encode()
    s.sendall(struct.pack(">BI", ftype, len(payload)) + payload)


def _read_exact(s, n):
    buf = b""
    while len(buf) < n:
        chunk = s.recv(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return buf


def recv_frame(s):
    """(type, payload bytes), or (None, None) when the peer closes before a whole frame arrives."""
    head = _read_exact(s, 5)
    if head is None:
        return None, None
    ftype, length = struct.unpack(">BI", head)
    body = _read_exact(s, length) if length else b""
    if body is None:
        return None, None
    return ftype, body


def recv(s):
    ftype, body = recv_frame(s)
    return ftype, None if body is None else body.decode(errors="replace")


def hello(path, accepts_starting=False, timeout=5.0):
    """One Hello exchange; returns (reply dict or None, seconds)."""
    start = time.perf_counter()
    with connect(path, timeout) as s:
        send(s, HELLO, json.dumps({"client_name": "ipc-baseline", "accepts_starting": True}) if accepts_starting else "")
        ftype, text = recv(s)
    took = time.perf_counter() - start
    return (json.loads(text) if ftype == HELLO_REPLY else None), took


def summary(samples_s):
    ms = sorted(x * 1000 for x in samples_s)
    if not ms:
        return {"n": 0}
    return {
        "n": len(ms),
        "p50_ms": round(statistics.median(ms), 3),
        "p95_ms": round(ms[min(len(ms) - 1, int(len(ms) * 0.95))], 3),
        "max_ms": round(ms[-1], 3),
    }
