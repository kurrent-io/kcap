#!/usr/bin/env python3
"""Usage: pcap_http.py CAPTURE.pcap [PORT]
Reassembles plain HTTP/1.1 on a loopback capture and prints one line per request: seconds from the first packet,
method, path, the request's Accept-Encoding, response status, Content-Encoding and body bytes on the wire (chunk
framing included). Then the totals: requests per route and bytes received. Messages are split by their framing
(Content-Length, chunked, or a response running to the end of the connection), never by text inside a body.
Standard library only; reads classic pcap with the BSD loopback link type."""
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


REQUEST = re.compile(rb"([!#$%&'*+.^_`|~0-9A-Za-z-]+) (\S+) HTTP/1\.1\r\n((?:[^\r\n]+\r\n)*)\r\n")
# int(..., 16) also takes a sign, "0x" and "_"; a negative size would never advance the cursor.
CHUNK_SIZE = re.compile(rb"[0-9A-Fa-f]+")
RESPONSE = re.compile(rb"HTTP/1\.1 (\d{3})[^\r\n]*\r\n((?:[^\r\n]+\r\n)*)\r\n")


def header(headers, name):
    m = re.search(rb"(?im)^" + name + rb": *(.*?) *$", headers)
    return m.group(1).decode().strip() if m else None


def chunked_end(data, pos):
    """End of a chunked body starting at pos, trailers included; the end of data when it is cut short."""
    while True:
        eol = data.find(b"\r\n", pos)
        if eol < 0:
            return len(data)
        token = data[pos:eol].split(b";")[0].strip(b" \t")
        if not CHUNK_SIZE.fullmatch(token):
            return len(data)
        size = int(token, 16)
        pos = eol + 2
        if size == 0:
            end = data.find(b"\r\n\r\n", pos - 2)
            return len(data) if end < 0 else end + 4
        pos += size + 2


def messages(data, head, headers_group, bodyless=lambda i, m: False, to_close=False):
    """(match, body end) for each message in data, in order. A start that does not parse is skipped to the next
    message head, so a capture that begins mid-connection still lines up from its first whole message."""
    pos, i = 0, 0
    while True:
        m = head.match(data, pos) or head.search(data, pos)
        if not m:
            return
        hdrs = m.group(headers_group)
        length = header(hdrs, rb"content-length")
        if bodyless(i, m):
            end = m.end()
        elif (header(hdrs, rb"transfer-encoding") or "").lower().endswith("chunked"):
            end = chunked_end(data, m.end())
        elif length is not None and length.isdigit():
            end = min(len(data), m.end() + int(length))
        else:
            end = len(data) if to_close else m.end()
        yield m, end
        pos, i = max(end, m.end()), i + 1


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
        for m, _ in messages(req_bytes, REQUEST, 3):
            sent = req[bisect.bisect_right(offsets, m.start()) - 1][0]
            mine.append([client, round(sent - first, 2), m.group(1).decode(), m.group(2).decode(),
                         header(m.group(3), rb"accept-encoding") or "-"])
        methods, final = [r[2] for r in mine], []

        def bodyless(_, m):
            if m.group(1).startswith(b"1"):
                return True
            final.append(m)
            return m.group(1) in (b"204", b"304") or (len(final) <= len(methods) and methods[len(final) - 1] == "HEAD")

        statuses = [s for s in messages(resp_bytes, RESPONSE, 2, bodyless, to_close=True) if not s[0].group(1).startswith(b"1")]
        rows += mine
        for i, r in enumerate(mine):
            if i < len(statuses):
                m, end = statuses[i]
                body = end - m.end()
                r += [m.group(1).decode(), header(m.group(2), rb"content-encoding") or "-", body]
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
