#!/usr/bin/env python3
"""Check tracked source and build files against the comment rules in CLAUDE.md."""

import argparse
from collections import Counter
import io
import json
from pathlib import Path
import re
import subprocess
import sys
import tokenize


BASELINE = "scripts/comment-rules-baseline.json"
EXTENSIONS = {
    ".cs", ".c", ".h", ".axaml", ".csproj", ".props", ".targets",
    ".sh", ".yml", ".yaml", ".py",
}
RULES = {
    "spec-coordinate": r"§\s*\d|\b(?:Task|Finding|Decision)[\s-]+#?[A-D]?\d+[a-z]?\b|\bPhase[- ]+(?:[A-D](?:\d+(?:-[a-z])?)?|\d+)\b|\(D\d+[a-z]?\)",
    "review-artifact": r"\b(?:Qodo|Bugbot|CodeRabbit|Codex review)\b|\bround[- ]?\d+\s+(?:follow-up\s+(?:review|fix|findings?)|review|fix|finding)\b|\bcode[- ]review\s+r\d+\b|\bBLOCKER-?\d|\b(?:review fix|Fix)\s+#?\d+\b|\b(?:PR|GH|GitHub)\s+#\d+\s+(?:review|finding)\b",
    "change-narration": r"\bpre-fix\b|\ban earlier (?:revision|version|draft)\b",
}
RULES = {name: re.compile(pattern, re.IGNORECASE) for name, pattern in RULES.items()}
RFC_SECTION = re.compile(r"\bRFC\s+\d+\s+§\s*\d+(?:\.\d+)*", re.IGNORECASE)
LINEAR_ID = re.compile(r"(?<![A-Za-z0-9_])AI-\d+(?![A-Za-z0-9_])")
SUPPRESSION = re.compile(r"//\s*linear-id-ok:\s*\S")


def git(*args):
    return subprocess.run(["git", *args], check=True, stdout=subprocess.PIPE).stdout


def c_comments(source):
    token = re.compile(r'//[^\n]*|/\*[\s\S]*?(?:\*/|\Z)|\$*"{3,}|(?:\$@|@\$|@|\$)?"|\'(?:\\.|[^\'\\\n])\'|[{}]')
    spans = []

    def skip_token(match):
        start, end = match.span()
        value = match.group()
        if value.startswith(("//", "/*")):
            spans.append((start, value))
        elif value.lstrip('$').startswith('"""'):
            closing = re.search('"{' + str(len(value.lstrip('$'))) + ',}', source[end:])
            end = end + closing.end() if closing else len(source)
        elif value.endswith('"'):
            string_end = re.compile(r'""|"|{{|{' if '@' in value else r'\\[\s\S]|"|\n|{{|{')
            while closing := string_end.search(source, end):
                end = closing.end()
                part = closing.group()
                if part == '{' and '$' in value:
                    end = skip_expression(end)
                elif part in ('"', '\n'):
                    break
            else:
                end = len(source)
        return end

    def skip_expression(offset):
        depth = 1
        while match := token.search(source, offset):
            if match.group() == '{':
                depth += 1
            elif match.group() == '}':
                depth -= 1
                if depth == 0:
                    return match.end()
            offset = skip_token(match)
        return len(source)

    offset = 0
    while match := token.search(source, offset):
        end = skip_token(match)
        offset = end
    for start, value in spans:
        body = value[2:]
        if value.startswith("/*") and body.endswith("*/"):
            body = body[:-2]
        line = source.count("\n", 0, start) + 1
        for index, text in enumerate(body.splitlines()):
            yield line + index, text


def xml_comments(source):
    for match in re.finditer(r"<!\[CDATA\[[\s\S]*?\]\]>|<!--[\s\S]*?-->", source):
        if match.group().startswith("<!--"):
            line = source.count("\n", 0, match.start()) + 1
            for index, text in enumerate(match.group()[4:-3].splitlines()):
                yield line + index, text


def hash_comments(source, shell):
    quote = None
    heredocs = []
    yaml_indent = None
    arithmetic = False
    for number, line in enumerate(source.splitlines(), 1):
        if heredocs:
            delimiter, strip_tabs = heredocs[0]
            if (line.lstrip("\t") if strip_tabs else line) == delimiter:
                heredocs.pop(0)
            continue
        indent = len(line) - len(line.lstrip())
        if yaml_indent is not None:
            if not line.strip() or indent > yaml_indent:
                continue
            yaml_indent = None
        index = 0
        while index < len(line):
            char = line[index]
            if char == "\\" and quote != "'":
                index += 2
                continue
            if quote:
                if char == quote:
                    if not shell and quote == "'" and line[index:index + 2] == "''":
                        index += 2
                        continue
                    quote = None
            elif char == "#" and (index == 0 or line[index - 1].isspace() or (shell and line[index - 1] in ";|&()")):
                yield number, line[index + 1:]
                break
            elif char in "\"'" and (shell or index == 0 or line[index - 1].isspace() or line[index - 1] in ":,[{"):
                quote = char
            elif shell and line[index:index + 3] == "<<<":
                index += 3
                continue
            elif shell and line[index:index + 2] == "((":
                arithmetic = True
                index += 2
                continue
            elif shell and line[index:index + 2] == "))":
                arithmetic = False
                index += 2
                continue
            elif shell and not arithmetic and line[index:index + 2] == "<<":
                match = re.match(r"<<(-?)\s*(?:'([^']+)'|\"([^\"]+)\"|\\?([A-Za-z_][A-Za-z_0-9]*))", line[index:])
                if match:
                    heredocs.append((next(value for value in match.groups()[1:] if value is not None), bool(match[1])))
                    index += match.end()
                    continue
            elif not shell and char in "|>" and re.match(r"[|>][+-]?[1-9]?\s*(?:#.*)?$", line[index:]):
                yaml_indent = indent
            index += 1


def comments(path, source):
    suffix = path.suffix
    if suffix in {".cs", ".c", ".h"}:
        return c_comments(source)
    if suffix in {".axaml", ".csproj", ".props", ".targets"}:
        return xml_comments(source)
    if suffix == ".py":
        return ((t.start[0], t.string[1:]) for t in tokenize.generate_tokens(io.StringIO(source).readline) if t.type == tokenize.COMMENT)
    return hash_comments(source, shell=suffix == ".sh")


def read_baseline(content):
    rows = json.loads(content)
    if not isinstance(rows, list):
        raise ValueError("baseline must be an array")
    result = Counter()
    for row in rows:
        if not isinstance(row, dict) or set(row) != {"path", "rule", "comment", "count"} or row["rule"] not in RULES:
            raise ValueError("invalid baseline entry")
        key = (row["path"], row["rule"], row["comment"])
        if not all(isinstance(value, str) for value in key) or key in result:
            raise ValueError("invalid or duplicate baseline entry")
        if type(row["count"]) is not int or row["count"] < 1:
            raise ValueError("invalid baseline count")
        result[key] = row["count"]
    return result


def write_baseline(path, hits):
    rows = [json.dumps(dict(path=p, rule=r, comment=c, count=count), ensure_ascii=False) for (p, r, c), count in sorted(hits.items())]
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("[\n" + ",\n".join(rows) + "\n]\n", encoding="utf-8")


def check(args):
    root = Path(git("rev-parse", "--show-toplevel").decode().strip())
    baseline_path = root / BASELINE
    hits = Counter()
    locations = {}
    violations = []
    paths = git("-C", str(root), "ls-files", "-z").decode().split("\0")
    for name in paths:
        path = Path(name)
        if path.suffix not in EXTENSIONS or name.startswith("docs/superpowers/"):
            continue
        full_path = root / path
        if full_path.is_symlink():
            raise ValueError(f"cannot scan symlink: {name}")
        source = full_path.read_text(encoding="utf-8-sig")
        for line, text in enumerate(source.splitlines(), 1):
            if LINEAR_ID.search(text) and not (name.startswith("test/") and path.suffix == ".cs" and SUPPRESSION.search(text)):
                violations.append(f"{name}:{line}: linear-id: {text.strip()}")
        if args.linear_ids_only:
            continue
        for line, text in comments(path, source):
            text = text.strip().lstrip("/*").strip()
            for rule, pattern in RULES.items():
                if pattern.search(RFC_SECTION.sub("", text)):
                    key = (name, rule, text)
                    hits[key] += 1
                    locations.setdefault(key, []).append(line)

    if not args.linear_ids_only:
        baseline = read_baseline(baseline_path.read_text(encoding="utf-8")) if baseline_path.exists() else None
        if baseline is None and not args.write_baseline:
            raise ValueError(f"missing {BASELINE}")
        if args.baseline_ref:
            ref = args.baseline_ref
            if not re.fullmatch(r"[0-9a-fA-F]{40,64}", ref):
                raise ValueError("--baseline-ref must be a full commit SHA")
            if git("-C", str(root), "ls-tree", ref, "--", BASELINE):
                previous = read_baseline(git("-C", str(root), "show", f"{ref}:{BASELINE}").decode())
                for key in sorted((baseline or Counter()) - previous):
                    violations.append(f"{BASELINE}: baseline may only shrink: {key[0]} ({key[1]}): {key[2]}")
        for key, count in sorted(hits.items()):
            allowed = baseline[key] if baseline is not None else count
            for line in locations[key][allowed:]:
                violations.append(f"{key[0]}:{line}: {key[1]}: {key[2]}")
        if baseline is not None and baseline - hits and not args.write_baseline:
            violations.append(f"{BASELINE}: stale entries; run bash scripts/check-comment-rules.sh --write-baseline to prune them")
        if args.write_baseline and not violations:
            write_baseline(baseline_path, hits)

    if violations:
        print("\n".join(violations))
        print("Comment hygiene failed. See CLAUDE.md → Comments and CONTRIBUTING.md.")
        return 1
    print("Comment hygiene: clean" + ("" if args.linear_ids_only else f" ({sum(hits.values())} baselined rule hits)"))
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--linear-ids-only", action="store_true")
    parser.add_argument("--write-baseline", action="store_true", help="Create a missing baseline or prune resolved entries; never add to an existing baseline")
    parser.add_argument("--baseline-ref", help="Reject baseline growth relative to this commit SHA")
    args = parser.parse_args()
    if args.linear_ids_only and (args.write_baseline or args.baseline_ref):
        parser.error("baseline options cannot be used with --linear-ids-only")
    try:
        return check(args)
    except (OSError, ValueError, TypeError, SyntaxError, subprocess.CalledProcessError, tokenize.TokenError) as error:
        print(f"Comment hygiene checker failed: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
