#!/usr/bin/env python3
"""Usage: coldstart_report.py DIR [--window "New session"] [--rows "Turn 0"]
Per run of coldstart.sh: when the window and the session rows first appeared on screen (as a range between the frame
before and the frame that showed them, seconds after launch), CPU seconds at 10 s, and every lifecycle mark the app
wrote that carries since_start_ms, with the phase durations of startup_phases. Then medians."""
import argparse
import json
import os
import statistics
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from frames import first_seen, ocr_frames


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dir")
    ap.add_argument("--window", default="New session")
    ap.add_argument("--rows", default="tighten the retry")
    a = ap.parse_args()

    runs = []
    for name in sorted(os.listdir(a.dir), key=lambda n: int(n[3:]) if n[3:].isdigit() else 0):
        run_dir = os.path.join(a.dir, name)
        if not name.startswith("run"):
            continue
        t0 = float(open(os.path.join(run_dir, "t0")).read())
        frames_dir = os.path.join(run_dir, "frames")
        frames = ocr_frames(frames_dir) if os.path.isdir(frames_dir) else []
        row = {"run": name}
        for key, needle in (("window", a.window), ("rows", a.rows)):
            found, seen, before = first_seen(frames, needle)
            row[key] = [round((before or t0) - t0, 2), round(seen - t0, 2) if seen is not None else None] if found else None
        cpu = open(os.path.join(run_dir, "cpu10")).read().strip()
        row["cpu_s_at_10s"] = float(cpu) if cpu else None
        marks = {}
        path = os.path.join(run_dir, "lifecycle.jsonl")
        if os.path.exists(path):
            for line in open(path):
                ev = json.loads(line)
                if ev.get("event") == "startup_phases":
                    marks.update({k: int(v) for k, v in ev.items() if k.endswith("_ms") and k != "since_start_ms"})
                elif "since_start_ms" in ev:
                    marks[ev["event"]] = int(ev["since_start_ms"])
        row["marks"] = marks
        runs.append(row)
        print(json.dumps(row))

    def median(values):
        values = [v for v in values if v is not None]
        return round(statistics.median(values), 2) if values else None

    summary = {"runs": len(runs),
               "window_median_s": median([r["window"][1] for r in runs if r["window"]]),
               "rows_median_s": median([r["rows"][1] for r in runs if r["rows"]]),
               "cpu_s_at_10s_median": median([r["cpu_s_at_10s"] for r in runs])}
    for mark in sorted({m for r in runs for m in r["marks"]}):
        summary[f"{mark}_median"] = median([r["marks"].get(mark) for r in runs])
    print(json.dumps(summary))


if __name__ == "__main__":
    main()
