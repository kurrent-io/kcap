#!/usr/bin/env python3
"""Scans the desktop app's AXAML for pointer targets under 24x24 and controls with no accessible name.

A control counts as named when it sets AutomationProperties.Name or LabeledBy, carries text Content
or a Header, or (for a button) holds a TextBlock. A fixed size is a literal Width or Height on a
Button, ToggleButton or RadioButton. Exits non-zero when it finds either.
Usage: python3 tools/ui-audit/axaml_a11y.py
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "src/Capacitor.App"
MIN_TARGET = 24
TARGETS = ("Button", "ToggleButton", "RadioButton")
NAMED_KINDS = ("TextBox", "ListBox", "ComboBox", "ToggleSwitch", "CheckBox", "Slider", "NumericUpDown", "TabItem")
# Named in code-behind (AutomationProperties.SetName), which this scan cannot read.
NAMED_IN_CODE = {"RailToggleButton", "bookmarkAsk", "topicAsk"}
STYLE_NAME = re.compile(r'<Style Selector="([^"]+)">((?:(?!</Style>).)*?)<Setter Property="AutomationProperties.Name"', re.S)
TAG = re.compile(r"<(?P<kind>[A-Za-z]+)(?P<attrs>(?:\s+[^<>]*?)?)(?P<end>/?)>", re.S)


def attr(attrs, name):
    m = re.search(r'(?<![\w.])' + re.escape(name) + r'="([^"]*)"', attrs)
    return m[1] if m else None


def named(text, match, styled):
    attrs = match["attrs"]
    if attr(attrs, "AutomationProperties.Name") or attr(attrs, "AutomationProperties.LabeledBy"):
        return True
    classes = set((attr(attrs, "Classes") or "").split())
    if classes & (styled | NAMED_IN_CODE) or attr(attrs, "x:Name") in NAMED_IN_CODE:
        return True
    if attr(attrs, "Content") or attr(attrs, "Header") or attr(attrs, "PlaceholderText"):
        return True
    if match["end"] == "/":
        return False
    close = text.find(f"</{match['kind']}>", match.end())
    body = text[match.end():close if close >= 0 else match.end()]
    return "<TextBlock" in body or f"<{match['kind']}.Header>" in body


def main():
    small, unnamed = [], []
    paths = [p for p in sorted(APP.rglob("*.axaml")) if "/obj/" not in str(p) and "/bin/" not in str(p)]
    # Classes whose style sets the accessible name.
    styled = set()
    for path in paths:
        for selector, _ in STYLE_NAME.findall(path.read_text()):
            # The first class of the last compound: "Button.topicHere.on" names topicHere, not "on".
            first = re.search(r"\.([A-Za-z]\w*)", selector.split(",")[0].split(" ")[-1])
            if first:
                styled.add(first[1])
    for path in paths:
        text = path.read_text()
        for m in TAG.finditer(text):
            line = text.count("\n", 0, m.start()) + 1
            where = f"{path.relative_to(ROOT)}:{line}"
            kind, attrs = m["kind"], m["attrs"]
            if kind in TARGETS:
                for dim in ("Width", "Height"):
                    v = attr(attrs, dim)
                    if v and re.fullmatch(r"\d+(\.\d+)?", v) and float(v) < MIN_TARGET:
                        small.append(f"{where}  {kind} {dim}={v}")
            if kind in NAMED_KINDS + TARGETS and attr(attrs, "IsVisible") != "False" and not named(text, m, styled):
                unnamed.append(f"{where}  {kind} {attr(attrs, 'x:Name') or ''}".rstrip())
    print(f"Fixed targets under {MIN_TARGET}x{MIN_TARGET}: {len(small)}")
    for s in small:
        print("  " + s)
    print(f"Controls with no name source: {len(unnamed)}")
    for u in unnamed:
        print("  " + u)
    return 1 if small or unnamed else 0


if __name__ == "__main__":
    sys.exit(main())
