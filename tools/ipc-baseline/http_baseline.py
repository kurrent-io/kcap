#!/usr/bin/env python3
"""Response sizes and latency of the server's read endpoints, with and without compression.

Usage: http_baseline.py [--url http://localhost:9473] [--runs N] [--token BEARER] [PATH ...]
For each path it reports p50 latency on a warm connection, the body size served plain and with
Accept-Encoding: gzip, br, and the Content-Encoding the server chose. A local server with auth provider None needs
no token."""
import argparse
import http.client
import json
import statistics
import time
import urllib.parse

DEFAULT_PATHS = ["/healthz", "/healthz/embeddings", "/api/agents/model-options", "/api/next-work", "/api/sessions?limit=50"]


def get(conn, path, headers):
    start = time.perf_counter()
    conn.request("GET", path, headers=headers)
    r = conn.getresponse()
    body = r.read()
    return time.perf_counter() - start, r.status, len(body), r.getheader("Content-Encoding")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("paths", nargs="*")
    ap.add_argument("--url", default="http://localhost:9473")
    ap.add_argument("--runs", type=int, default=30)
    ap.add_argument("--token")
    a = ap.parse_args()
    u = urllib.parse.urlparse(a.url)
    conn = (http.client.HTTPSConnection if u.scheme == "https" else http.client.HTTPConnection)(u.hostname, u.port)
    auth = {"Authorization": f"Bearer {a.token}"} if a.token else {}

    report = {}
    for path in a.paths or DEFAULT_PATHS:
        _, status, plain, _ = get(conn, path, auth)
        _, _, packed, encoding = get(conn, path, {**auth, "Accept-Encoding": "gzip, br"})
        times = [get(conn, path, auth)[0] for _ in range(a.runs)]
        report[path] = {"status": status, "plain_bytes": plain, "compressed_bytes": packed, "content_encoding": encoding,
                        "p50_ms": round(statistics.median(times) * 1000, 2)}
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
