#!/usr/bin/env python3
"""Response sizes and latency of the server's read endpoints, with and without compression.

Usage: http_baseline.py [--url http://localhost:9473] [--runs N] [--token BEARER] [PATH ...]
For each path it reports p50 latency on a warm connection, the body size served plain and with
Accept-Encoding: gzip, br, and the Content-Encoding the server chose. A local server with auth provider None needs
no token."""
import argparse
import http.client
import ipaddress
import json
import statistics
import time
import urllib.parse

DEFAULT_PATHS = ["/healthz", "/healthz/embeddings", "/api/agents/model-options", "/api/next-work", "/api/sessions?limit=50"]


def get(conn, path, headers):
    """(seconds, status, body bytes, Content-Encoding); status 0 when no whole response arrived in time."""
    start = time.perf_counter()
    try:
        conn.request("GET", path, headers=headers)
        r = conn.getresponse()
        body = r.read()
    except (OSError, http.client.HTTPException):
        conn.close()
        return time.perf_counter() - start, 0, 0, None
    return time.perf_counter() - start, r.status, len(body), r.getheader("Content-Encoding")


def loopback(host):
    if host == "localhost":
        return True
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("paths", nargs="*")
    ap.add_argument("--url", default="http://localhost:9473")
    ap.add_argument("--runs", type=int, default=30)
    ap.add_argument("--token")
    ap.add_argument("--timeout", type=float, default=30)
    a = ap.parse_args()
    u = urllib.parse.urlparse(a.url)
    if a.token and u.scheme != "https" and not loopback(u.hostname):
        raise SystemExit("--token goes only to https or a loopback server: plain http would send it in the clear")
    conn = (http.client.HTTPSConnection if u.scheme == "https" else http.client.HTTPConnection)(u.hostname, u.port, timeout=a.timeout)
    auth = {"Authorization": f"Bearer {a.token}"} if a.token else {}

    report = {}
    for path in a.paths or DEFAULT_PATHS:
        _, status, plain, _ = get(conn, path, auth)
        _, packed_status, packed, encoding = get(conn, path, {**auth, "Accept-Encoding": "gzip, br"})
        timed = [get(conn, path, auth) for _ in range(a.runs)]
        # Only 2xx responses are the endpoint; an error page or a timeout is counted, never measured.
        times = [t for t, code, _, _ in timed if 200 <= code < 300]
        report[path] = {"status": status, "compressed_status": packed_status,
                        "plain_bytes": plain, "compressed_bytes": packed, "content_encoding": encoding,
                        "failed_runs": len(timed) - len(times),
                        "p50_ms": round(statistics.median(times) * 1000, 2) if times else None}
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
