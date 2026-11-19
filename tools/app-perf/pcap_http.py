#!/usr/bin/env python3
"""Usage: pcap_http.py CAPTURE.pcap [PORT]
Reassembles plain HTTP/1.1 on a loopback capture and prints one line per request: seconds from the first packet,
method, path, the request's Accept-Encoding, response status, Content-Encoding and body bytes on the wire. Then the
totals: requests per route and bytes received. Standard library only; reads classic pcap with the BSD loopback
link type."""
import bisect
import collections
import re
import struct
import sys


def packets(path):
    with open(path, "rb") as f:
        magic = f.read(24)
        endian = "<" if magic[:4] in (b"\xd4\xc3\xb2\xa1", b"\x4d\x3c\xb2\xa1") else ">"
        nano = magic[:4] in (b"\x4d\x3c\xb2\xa1", b"\xa1\xb2\x3c\x4d")
        while True:
            head = f.read(16)
            if len(head) < 16:
                return
            sec, frac, incl, _ = struct.unpack(endian + "IIII", head)
            data = f.read(incl)
            yield sec + frac / (1e9 if nano else 1e6), data


def tcp_segments(path, port):
    for ts, data in packets(path):
        ip = data[4:]
        if not ip or ip[0] >> 4 != 4:
            if ip and ip[0] >> 4 == 6:
                proto, ihl, src, dst = ip[6], 40, ip[8:24], ip[24:40]
            else:
                continue
        else:
            ihl = (ip[0] & 15) * 4
            proto, src, dst = ip[9], ip[12:16], ip[16:20]
        if proto != 6:
            continue
        tcp = ip[ihl:]
        sport, dport, seq = struct.unpack(">HHI", tcp[:8])
        off = (tcp[12] >> 4) * 4
        payload = tcp[off:]
        if payload:
            yield ts, sport, dport, seq, payload


def main():
    path = sys.argv[1]
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 9473
    # Arrival order, not sequence order: an initial sequence number near 2**32 wraps, and loopback does not reorder.
    streams = collections.defaultdict(dict)
    first = None
    for ts, sport, dport, seq, payload in tcp_segments(path, port):
        first = first or ts
        key = (sport, "req") if dport == port else (dport, "resp")
        streams[key].setdefault(seq, (ts, payload))

    def joined(key):
        segs = streams.get(key, {})
        return sorted(segs.values(), key=lambda v: v[0])

    rows, per_route, received = [], collections.Counter(), 0
    for (client, kind) in list(streams):
        if kind != "req":
            continue
        req = joined((client, "req"))
        resp = joined((client, "resp"))
        req_bytes = b"".join(p for _, p in req)
        resp_bytes = b"".join(p for _, p in resp)
        offsets = [0]
        for _, p in req[:-1]:
            offsets.append(offsets[-1] + len(p))
        mine = []
        for m in re.finditer(rb"(?:^|(?<=\n))([!#$%&'*+.^_`|~0-9A-Za-z-]+) (\S+) HTTP/1\.1\r\n(.*?)\r\n\r\n", req_bytes, re.S):
            enc = re.search(rb"(?im)^accept-encoding: *(.*)$", m.group(3))
            sent = req[bisect.bisect_right(offsets, m.start(1)) - 1][0]
            mine.append([client, m.group(1).decode(), m.group(2).decode(), enc.group(1).decode().strip() if enc else "-"])
            mine[-1].insert(1, round(sent - first, 2))
        statuses = list(re.finditer(rb"HTTP/1\.1 (\d{3})[^\r]*\r\n(.*?)\r\n\r\n", resp_bytes, re.S))
        rows += mine
        for i, r in enumerate(mine):
            if i < len(statuses):
                hdrs = statuses[i].group(2)
                ce = re.search(rb"(?im)^content-encoding: *(.*)$", hdrs)
                end = statuses[i + 1].start() if i + 1 < len(statuses) else len(resp_bytes)
                body = end - statuses[i].end()
                r += [statuses[i].group(1).decode(), ce.group(1).decode().strip() if ce else "-", body]
                received += body
            else:
                r += ["?", "-", 0]
    for r in sorted(rows, key=lambda r: r[1]):
        _, t, method, target, accept, status, ce, size = r
        route = re.sub(r"[0-9a-f]{32}", "{id}", target.split("?")[0])
        per_route[f"{method} {route}"] += 1
        print(f"{t:7.2f}  {method:5} {target[:90]:90} ae={accept[:20]:20} {status} ce={ce:4} {size:>10,} B")
    print("\nper route:", dict(per_route))
    print(f"received: {received:,} B")


if __name__ == "__main__":
    main()
