#!/usr/bin/env python3
"""WCAG contrast table for the desktop app palette in src/Capacitor.App/App.axaml.

Exits non-zero when a text pair falls below 4.5:1 or a non-text pair below 3:1.
Usage: python3 tools/ui-audit/contrast.py [path/to/App.axaml]
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
AXAML = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "src/Capacitor.App/App.axaml"

SURFACES = ["KcapCanvasBrush", "KcapSurfaceBrush", "KcapSurfaceRaisedBrush"]
TEXT = ["KcapTextBrush", "KcapMutedBrush", "KcapFaintBrush", "KcapPlaceholderBrush"]
STATUS_FILLS = ["KcapSuccessDimBrush", "KcapWarningDimBrush", "KcapDangerDimBrush", "KcapInfoDimBrush", "KcapPurpleDimBrush"]
STATUS_TEXT = ["KcapMutedBrush", "KcapFaintBrush", "KcapDangerBrush", "KcapSuccessBrush", "KcapWarningBrush"]
EDGES = ["KcapFieldBorderBrush"]


def colours(text):
    found = {}
    for key, value in re.findall(r'x:Key="(\w+)"\s+Color="(#[0-9A-Fa-f]{6,8})"', text):
        found[key] = value
    return found


def rgb(hexvalue, behind=None):
    h = hexvalue.lstrip("#")
    if len(h) == 8:
        alpha, h = int(h[:2], 16) / 255, h[2:]
    else:
        alpha = 1.0
    c = [int(h[i:i + 2], 16) for i in (0, 2, 4)]
    if alpha < 1 and behind is not None:
        c = [round(a * alpha + b * (1 - alpha)) for a, b in zip(c, behind)]
    return c


def luminance(c):
    def lin(v):
        v /= 255
        return v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4
    r, g, b = (lin(v) for v in c)
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def ratio(a, b):
    la, lb = sorted((luminance(a), luminance(b)), reverse=True)
    return (la + 0.05) / (lb + 0.05)


def main():
    palette = colours(AXAML.read_text())
    missing = sorted(set(SURFACES + TEXT + STATUS_FILLS + STATUS_TEXT + EDGES) - palette.keys())
    if missing:
        print("Missing palette entries: " + ", ".join(missing))
        return 1
    failures = []

    def check(fg, bg, need):
        back = rgb(palette[bg])
        r = ratio(rgb(palette[fg], back), back)
        flag = "ok " if r >= need else "LOW"
        print(f"  {flag} {r:5.2f}:1  {fg:28} on {bg}")
        if r < need:
            failures.append((fg, bg, r, need))

    print("Text on surfaces (need 4.5:1)")
    for bg in SURFACES:
        for fg in TEXT:
            check(fg, bg, 4.5)
    print("Text on status fills (need 4.5:1)")
    for bg in STATUS_FILLS:
        for fg in STATUS_TEXT:
            check(fg, bg, 4.5)
    print("Field edges on surfaces (need 3:1)")
    for bg in SURFACES:
        for fg in EDGES:
            check(fg, bg, 3.0)
    if failures:
        print(f"\n{len(failures)} pair(s) below the threshold")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
