#!/usr/bin/env python3
"""Usage: view_report.py DIR [--header TEXT] [--transcript TEXT]
For one view_load.sh run: when the tab header and the transcript first appeared (a range between the frame before
and the frame that showed them, seconds after the click), and the CPU seconds the app spent over the run."""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from frames import first_seen, ocr_frames


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dir")
    ap.add_argument("--header", default="tighten the retry")
    ap.add_argument("--transcript", default="tests pass")
    a = ap.parse_args()
    t0 = float(open(os.path.join(a.dir, "t0")).read())
    frames = ocr_frames(os.path.join(a.dir, "frames"))
    out = {"dir": a.dir, "frames": len(frames)}
    for key, needle in (("header", a.header), ("transcript", a.transcript)):
        found, seen, before = first_seen(frames, needle)
        out[key] = [round((before or t0) - t0, 2), round(seen - t0, 2) if seen is not None else None] if found else None
    cpu_path = os.path.join(a.dir, "cpu")
    if os.path.exists(cpu_path):
        c0, c1 = map(float, open(cpu_path).read().split())
        out["cpu_s"] = round(c1 - c0, 2)
    print(json.dumps(out))


if __name__ == "__main__":
    main()
