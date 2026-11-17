#!/usr/bin/env python3
"""Round-trip latency of the daemon's local control socket.

Usage: socket_probe.py [N] [--name DAEMON]
Prints one JSON object: connect, Hello, first StatusSubscribe snapshot, List, AccountsSubscribe and ConsentRulesGet,
each with p50/p95/max over N runs and the reply size. The regression guard is p50 <= 0.5 ms for every operation."""
import argparse
import json
import time

import kcapipc as k


def one_shot(path, ftype, text=""):
    start = time.perf_counter()
    with k.connect(path) as s:
        k.send(s, ftype, text)
        rtype, body = k.recv_frame(s)
    return time.perf_counter() - start, rtype, len(body or b"")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("n", nargs="?", type=int, default=100)
    ap.add_argument("--name")
    a = ap.parse_args()
    path = k.socket_path(a.name)

    results, sizes, failures = {}, {}, {}
    for label, ftype, text in [
        ("hello", k.HELLO, ""),
        ("status_subscribe_first", k.STATUS_SUBSCRIBE, ""),
        ("list", k.LIST, ""),
        ("accounts_subscribe_first", k.ACCOUNTS_SUBSCRIBE, ""),
        ("consent_rules_get", k.CONSENT_RULES_GET, ""),
    ]:
        samples = []
        for _ in range(a.n):
            try:
                took, rtype, size = one_shot(path, ftype, text)
            except OSError:
                rtype = None
            # An error frame, a transport failure or a connection closed unanswered is a failure, never a sample.
            if rtype is None or rtype == k.ERROR:
                failures[label] = failures.get(label, 0) + 1
                break
            samples.append(took)
            sizes[label] = size
        results[label] = k.summary(samples)

    connects = []
    for _ in range(a.n):
        start = time.perf_counter()
        try:
            k.connect(path).close()
        except OSError:
            failures["connect"] = failures.get("connect", 0) + 1
            continue
        connects.append(time.perf_counter() - start)
    results["connect"] = k.summary(connects)

    print(json.dumps({"socket": path, "ops": results, "reply_bytes": sizes, "failures": failures}, indent=2))


if __name__ == "__main__":
    main()
