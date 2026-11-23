#!/usr/bin/env python3
"""Checks that desktop-app font sizes come from the type scale in App.axaml.

  python3 tools/ui-audit/type_scale.py          report literal FontSize values in AXAML and C#
  python3 tools/ui-audit/type_scale.py --fix    move literals onto the nearest scale step

Exits non-zero when a literal remains.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "src/Capacitor.App"

# Literal size -> scale key. Half-pixel sizes step down so a 12.5 / 13.5 pair keeps its order.
STEPS = {
    "Caption": 11, "Small": 12, "Body": 13, "Large": 15, "Heading": 19, "Title": 22, "Display": 28,
}
MAP = {
    8: "Caption", 9: "Caption", 9.5: "Caption", 10: "Caption", 10.5: "Caption", 11: "Caption",
    11.5: "Small", 12: "Small", 12.5: "Small",
    13: "Body", 13.5: "Body",
    14: "Large", 14.5: "Large", 15: "Large", 16: "Large",
    17.5: "Heading", 18: "Heading", 19: "Heading", 20: "Heading",
    22: "Title",
    26: "Display", 28: "Display", 30: "Display",
}

AXAML_ATTR = re.compile(r'FontSize="(\d+(?:\.\d+)?)"')
AXAML_SETTER_TAG = re.compile(r'<Setter\b[^>]*>', re.S)
SETTER_VALUE = re.compile(r'(\bValue\s*=\s*")(\d+(?:\.\d+)?)(")')
CS_LITERAL = re.compile(r'\bFontSize\s*=\s*(\d+(?:\.\d+)?)[dDfFmM]?(?![\w.])')


def setters(text, fix):
    """FontSize setters with a literal Value, whatever the attribute order or line breaks."""
    found = []

    def one(tag):
        t = tag[0]
        if not re.search(r'\bProperty\s*=\s*"FontSize"', t) or not (m := SETTER_VALUE.search(t)):
            return t
        found.append(t)
        return t[:m.start()] + f'{m[1]}{{StaticResource KcapFontSize{key(m[2])}}}{m[3]}' + t[m.end():] if fix else t

    return AXAML_SETTER_TAG.sub(one, text), found


def key(value):
    v = float(value)
    v = int(v) if v.is_integer() else v
    if v not in MAP:
        raise SystemExit(f"no scale step for FontSize {value}: add it to MAP")
    return MAP[v]


def files(pattern):
    return [p for p in APP.rglob(pattern) if "/obj/" not in str(p) and "/bin/" not in str(p)]


def main():
    fix = "--fix" in sys.argv
    left = []
    for path in files("*.axaml"):
        text = path.read_text()
        new = AXAML_ATTR.sub(lambda m: f'FontSize="{{StaticResource KcapFontSize{key(m[1])}}}"', text)
        new, literal_setters = setters(new, fix)
        if fix and new != text:
            path.write_text(new)
        elif not fix:
            left += [f"{path.relative_to(ROOT)}: {m[0]}" for m in AXAML_ATTR.finditer(text)]
            left += [f"{path.relative_to(ROOT)}: {' '.join(s.split())}" for s in literal_setters]
    for path in files("*.cs"):
        if path.name == "TypeScale.cs":
            continue
        text = path.read_text()
        new = CS_LITERAL.sub(lambda m: f"FontSize = TypeScale.{key(m[1])}", text)
        if new != text:
            if fix:
                path.write_text(new)
            else:
                left += [f"{path.relative_to(ROOT)}: {m[0]}" for m in CS_LITERAL.finditer(text)]
    for line in left:
        print(line)
    if left:
        print(f"\n{len(left)} literal font size(s) outside the type scale")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
