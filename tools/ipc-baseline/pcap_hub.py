#!/usr/bin/env python3
"""Reads a loopback tcpdump capture and lists each hub request and broadcast with its time and source port.

Usage: pcap_hub.py CAPTURE.pcap [--port 9473]
Prints JSON lines: {"t": epoch seconds, "src": port, "kind": "negotiate"|"upgrade"|"invoke", "target": ...}. Hub
invocations are read from the SignalR JSON protocol's text frames as they cross the wire unmasked server-side; a
masked client frame is skipped. A frame split across TCP segments is joined with its flow's previous segment. Map a source port to its process with `lsof -iTCP -n -P` taken while the connection is
open."""
import argparse
import json
import re
import struct

INVOKE = re.compile(rb'"type":1,(?:"invocationId":"[^"]*",)?"target":"([A-Za-z0-9]+)"')


def packets(path):
    with open(path, "rb") as f:
        head = f.read(24)
        magic = struct.unpack("<I", head[:4])[0]
        endian = "<" if magic in (0xA1B2C3D4, 0xA1B23C4D) else ">"
        nano = magic in (0xA1B23C4D, 0x4D3CB2A1)
        link = struct.unpack(endian + "I", head[20:24])[0]
        while True:
            rec = f.read(16)
            if len(rec) < 16:
                return
            sec, frac, incl, _ = struct.unpack(endian + "IIII", rec)
            data = f.read(incl)
            yield sec + frac / (1e9 if nano else 1e6), link, data


def tcp_payload(link, data):
    offset = 4 if link in (0, 108) else 14 if link == 1 else 0
    if len(data) <= offset:
        return None
    version = data[offset] >> 4
    if version == 4:
        ihl = (data[offset] & 0x0F) * 4
        ip_end = offset + ihl
        if ihl < 20 or len(data) < ip_end:
            return None
        proto = data[offset + 9]
    elif version == 6:
        ip_end = offset + 40
        if len(data) < ip_end:
            return None
        proto = data[offset + 6]
    else:
        return None
    if proto != 6 or len(data) < ip_end + 20:
        return None
    src, dst = struct.unpack(">HH", data[ip_end:ip_end + 4])
    data_off = (data[ip_end + 12] >> 4) * 4
    return src, dst, data[ip_end + data_off:]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("capture")
    ap.add_argument("--port", type=int, default=9473)
    a = ap.parse_args()
    # The tail of each flow's previous segment, so an invocation split across segments is still found once.
    carry = {}
    for t, link, data in packets(a.capture):
        parsed = tcp_payload(link, data)
        if not parsed or not parsed[2]:
            continue
        src, dst, body = parsed
        if dst == a.port and body.startswith(b"POST /hubs/sessions/negotiate"):
            print(json.dumps({"t": t, "src": src, "kind": "negotiate"}))
        elif dst == a.port and body.startswith(b"GET /hubs/sessions"):
            print(json.dumps({"t": t, "src": src, "kind": "upgrade"}))
        elif a.port in (src, dst):
            prev = carry.get((src, dst), b"")
            joined = prev + body
            for m in INVOKE.finditer(joined):
                if m.end() > len(prev):
                    print(json.dumps({"t": t, "src": src, "dst": dst, "kind": "invoke", "target": m.group(1).decode()}))
            carry[(src, dst)] = joined[-256:]


if __name__ == "__main__":
    main()
