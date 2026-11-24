#!/usr/bin/env python3
"""WCAG contrast table for the desktop app palette in src/Capacitor.App/Design/Palette.axaml.

Exits non-zero when a text pair falls below 4.5:1 or a non-text pair below 3:1.
Usage: python3 tools/ui-audit/contrast.py [path/to/palette.axaml]
"""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
AXAML = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "src/Capacitor.App/Design/Palette.axaml"

SURFACES = ["KcapCanvasBrush", "KcapSurfaceBrush", "KcapSurfaceRaisedBrush"]
TEXT = ["KcapTextBrush", "KcapMutedBrush", "KcapFaintBrush", "KcapPlaceholderBrush"]
STATUS_ROLES = ["Success", "Warning", "Danger", "Info", "Purple"]
STATUS_INKS = [f"Kcap{role}Brush" for role in STATUS_ROLES]
STATUS_FILLS = [f"Kcap{role}DimBrush" for role in STATUS_ROLES]
STATUS_TEXT = ["KcapMutedBrush", "KcapFaintBrush", "KcapDangerBrush", "KcapSuccessBrush", "KcapWarningBrush"]
EDGES = ["KcapFieldBorderBrush"]


def colours(text):
    found = {}
    for key, value in re.findall(r'x:Key="(\w+)"\s+Color="(#[0-9A-Fa-f]{6,8})"', text):
        found[key] = value
    return found


def rgb(hexvalue, behind=None, opacity=1.0):
    h = hexvalue.lstrip("#")
    if len(h) == 8:
        alpha, h = int(h[:2], 16) / 255, h[2:]
    else:
        alpha = 1.0
    alpha *= opacity
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


def audit(palette, opacities, edges=EDGES):
    missing = sorted(set(SURFACES + TEXT + STATUS_INKS + STATUS_FILLS + STATUS_TEXT + edges) - palette.keys())
    if missing:
        print("Missing palette entries: " + ", ".join(missing))
        return 1
    failures = []

    def check(fg, bg, need):
        fill = palette[bg]
        backing_surfaces = SURFACES if len(fill) == 9 and int(fill[1:3], 16) < 255 else [None]
        for surface in backing_surfaces:
            back = rgb(fill, rgb(palette[surface]) if surface else None)
            background = f"{bg} over {surface}" if surface else bg
            r = ratio(rgb(palette[fg], back, opacities.get(fg, 1.0)), back)
            flag = "ok " if r >= need else "LOW"
            print(f"  {flag} {r:5.2f}:1  {fg:28} on {background}")
            if r < need:
                failures.append((fg, background, r, need))

    print("Text on surfaces (need 4.5:1)")
    for bg in SURFACES:
        for fg in [*TEXT, *STATUS_INKS]:
            check(fg, bg, 4.5)
    print("Text on status fills (need 4.5:1)")
    for bg in STATUS_FILLS:
        for fg in STATUS_TEXT:
            check(fg, bg, 4.5)
    for fg, bg in zip(STATUS_INKS, STATUS_FILLS):
        if fg not in STATUS_TEXT:
            check(fg, bg, 4.5)
    print("Field edges on surfaces (need 3:1)")
    for bg in SURFACES:
        for fg in edges:
            check(fg, bg, 3.0)
    for fill in ["KcapPrimaryBrush", "KcapPrimaryHoverBrush", "KcapPrimaryPressedBrush"]:
        check("KcapOnPrimaryBrush", fill, 4.5)
    for fill in ["KcapControlHoverBrush", "KcapControlPressedBrush"]:
        if fill in palette:
            check("KcapTextBrush", fill, 4.5)
    for role in [*TEXT, *STATUS_INKS]:
        check(role, "KcapSelectionBrush", 4.5)
    if failures:
        print(f"\n{len(failures)} pair(s) below the threshold")
        return 1
    return 0


if __name__ == "__main__":
    base = colours(AXAML.read_text())
    key = "{http://schemas.microsoft.com/winfx/2006/xaml}Key"
    opacities = {entry.attrib[key]: float(entry.attrib["Opacity"])
                 for entry in ET.parse(AXAML).getroot().iter() if key in entry.attrib and "Opacity" in entry.attrib}
    result = audit(base, opacities)
    if len(sys.argv) == 1:
        for variant in ET.parse(AXAML.with_name("ClassicPreview.axaml")).getroot():
            print(f"\n{variant.attrib[key]}")
            overrides = {entry.attrib[key]: entry.text.strip() for entry in variant}
            result |= audit(base | overrides, opacities, [*EDGES, "KcapChoiceBorderBrush", "KcapTopicRailBrush"])
    sys.exit(result)
