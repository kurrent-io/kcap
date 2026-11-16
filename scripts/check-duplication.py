#!/usr/bin/env python3
import argparse
from collections import Counter, defaultdict
from difflib import SequenceMatcher
import os
from pathlib import Path
import re
import subprocess
import sys

try:
    from tree_sitter import Language, Parser
    import tree_sitter_c_sharp
except ImportError:
    print("duplication: install scripts/comment-rules-requirements.txt in a Python 3.10+ virtual environment", file=sys.stderr)
    sys.exit(2)


PARSER = Parser(Language(tree_sitter_c_sharp.language()))
BASELINE = "scripts/check-duplication.baseline"
SCOPE = "src/"
MIN_COPIES = 3
SIMILARITY = 0.9
# Non-whitespace characters: shorter bodies are accessors and one-line forwarders, which repeat by design.
MIN_BODY_CHARS = 110
EXACT_WINDOW = 10
RENAMED_WINDOW = 14
LITERALS = frozenset({
    "string_literal", "verbatim_string_literal", "raw_string_literal", "character_literal",
    "integer_literal", "real_literal", "boolean_literal", "null_literal", "interpolated_string_expression",
})
METHODS = frozenset({"method_declaration", "local_function_statement"})
DIRECTIVES = frozenset({"using_directive", "extern_alias_directive"})
NAMESPACES = frozenset({"namespace_declaration", "file_scoped_namespace_declaration"})
CODE = re.compile(r"[A-Za-z0-9]")


def tracked():
    out = subprocess.check_output(["git", "ls-files", "-z", "--", f"{SCOPE}*.cs"])
    return sorted(os.fsdecode(p) for p in out.split(b"\0") if p)


def parse(path):
    return PARSER.parse(Path(path).read_bytes()).root_node


def literal_text(node):
    """A node's source less any comment inside it, which only an interpolation can hold."""
    data = node.text
    cuts = []
    stack = list(node.children)
    while stack:
        current = stack.pop()
        if current.type == "comment":
            cuts.append((current.start_byte - node.start_byte, current.end_byte - node.start_byte))
        else:
            stack.extend(current.children)
    for start, end in sorted(cuts, reverse=True):
        data = data[:start] + data[end:]
    return data.decode("utf-8", "replace")


def tokens(node):
    """Yields (row, text, kind) per token, comments dropped; a literal, even a multi-line one, is one token."""
    stack = [node]
    while stack:
        current = stack.pop()
        if current.type == "comment":
            continue
        if current.type in LITERALS or current.child_count == 0:
            text = literal_text(current)
            if text:
                kind = "L" if current.type in LITERALS else "I" if current.type == "identifier" else ""
                yield current.start_point[0], text, kind
            continue
        stack.extend(reversed(current.children))


def methods(root):
    stack = [root]
    while stack:
        node = stack.pop()
        if node.type in METHODS:
            yield node
        stack.extend(node.children)


def signature(node):
    name = node.child_by_field_name("name")
    params = node.child_by_field_name("parameters")
    if name is None or params is None:
        return None
    types = []
    for i, child in enumerate(params.children):
        if child.type == "parameter":
            kind = child.child_by_field_name("type")
            types.append("".join(t for _, t, _ in tokens(kind)) if kind is not None else "?")
        elif params.field_name_for_child(i) == "type":
            # The grammar hangs a `params` array's type on the list itself, not on a parameter node.
            types.append("params " + "".join(t for _, t, _ in tokens(child)))
    return name.text.decode(), tuple(types)


def body(node):
    found = node.child_by_field_name("body")
    if found is None:
        found = next((c for c in node.named_children if c.type == "arrow_expression_clause"), None)
    return None if found is None else [t for _, t, _ in tokens(found)]


def similar(a, b):
    if 2 * min(len(a), len(b)) / (len(a) + len(b)) < SIMILARITY:
        return False
    matcher = SequenceMatcher(None, a, b, autojunk=False)
    return matcher.quick_ratio() >= SIMILARITY and matcher.ratio() >= SIMILARITY


def families(roots):
    """Methods sharing a name and parameter types whose token streams are 90% alike, in 3+ files."""
    by_signature = defaultdict(list)
    for path, root in roots.items():
        for node in methods(root):
            key = signature(node)
            text = body(node)
            if key and text and sum(len("".join(t.split())) for t in text) >= MIN_BODY_CHARS:
                by_signature[key].append((path, node.start_point[0] + 1, text))
    found = []
    for key, copies in by_signature.items():
        if len({path for path, _, _ in copies}) < MIN_COPIES:
            continue
        parent = list(range(len(copies)))

        def find(i):
            while parent[i] != i:
                parent[i] = parent[parent[i]]
                i = parent[i]
            return i

        for i in range(len(copies)):
            for j in range(i + 1, len(copies)):
                if copies[i][0] != copies[j][0] and find(i) != find(j) and similar(copies[i][2], copies[j][2]):
                    parent[find(j)] = find(i)
        groups = defaultdict(list)
        for i, (path, line, _) in enumerate(copies):
            groups[find(i)].append((path, line))
        found.extend((key, sorted(group)) for group in groups.values()
                     if len({path for path, _ in group}) >= MIN_COPIES)
    return sorted(found)


def directive_rows(root):
    rows = set()
    stack = [root]
    while stack:
        node = stack.pop()
        if node.type in DIRECTIVES:
            rows.update(range(node.start_point[0], node.end_point[0] + 1))
        elif node.type in NAMESPACES:
            name = node.child_by_field_name("name")
            if name is not None:
                rows.update(range(node.start_point[0], name.end_point[0] + 1))
            stack.extend(node.children)
        elif node.type in ("compilation_unit", "declaration_list"):
            stack.extend(node.children)
    return rows


def code_lines(root, renamed):
    rows = defaultdict(list)
    for row, text, kind in tokens(root):
        rows[row].append(kind if renamed and kind else text)
    return [(row, " ".join(rows[row])) for row in sorted(rows)]


def clone_lines(roots, window, renamed):
    """Per file: (code lines, lines inside a window of `window` code lines that recurs anywhere in scope).
    Using and namespace directives are not code; punctuation-only lines are counted but never start or fill a window."""
    files = {}
    for path, root in roots.items():
        skip = directive_rows(root)
        exact = code_lines(root, False)
        keep = [i for i, (row, _) in enumerate(exact) if row not in skip]
        lines = code_lines(root, True) if renamed else exact
        files[path] = (len(keep), [lines[i][1] for i in keep if CODE.search(exact[i][1])])
    seen = defaultdict(int)
    for _, lines in files.values():
        for i in range(len(lines) - window + 1):
            seen[tuple(lines[i:i + window])] += 1
    result = {}
    for path, (total, lines) in files.items():
        marked = [False] * len(lines)
        for i in range(len(lines) - window + 1):
            if seen[tuple(lines[i:i + window])] > 1:
                marked[i:i + window] = [True] * window
        result[path] = (total, sum(marked))
    return result


def report_clones(roots):
    exact = clone_lines(roots, EXACT_WINDOW, False)
    renamed = clone_lines(roots, RENAMED_WINDOW, True)
    scopes = defaultdict(lambda: [0, 0, 0])
    for path in roots:
        parts = path.split("/")
        for scope in {parts[0], "/".join(parts[:2])}:
            scopes[scope][0] += exact[path][0]
            scopes[scope][1] += exact[path][1]
            scopes[scope][2] += renamed[path][1]
    print(f"{'scope':<44} {'code lines':>10} {f'exact ({EXACT_WINDOW})':>11} {f'renamed ({RENAMED_WINDOW})':>13}")
    for scope, (total, ex, rn) in sorted(scopes.items()):
        print(f"{scope:<44} {total:>10} {ex:>11} {rn:>13}")


def label(key):
    name, types = key
    return f"{name}({', '.join(types)})"


def print_families(found):
    for key, copies in found:
        print(f"{label(key)} x{len(copies)}")
        for path, line in copies:
            print(f"    {path}:{line}")


def read_baseline():
    """The families allowed to stay, one signature per line; a signature listed twice allows two families."""
    return Counter(line.strip() for line in Path(BASELINE).read_text().splitlines() if line.strip())


def main():
    cli = argparse.ArgumentParser(description=f"Ratchet on C# method families copied into {MIN_COPIES}+ files under {SCOPE}.")
    mode = cli.add_mutually_exclusive_group()
    mode.add_argument("--list", action="store_true", help="print every family")
    mode.add_argument("--update", action="store_true", help=f"write the current families to {BASELINE}")
    mode.add_argument("--clones", action="store_true", help="print line-window clone counts per project")
    args = cli.parse_args()
    os.chdir(subprocess.check_output(["git", "rev-parse", "--show-toplevel"]).decode().rstrip("\n"))
    roots = {path: parse(path) for path in tracked()}
    if args.clones:
        report_clones(roots)
        return 0
    found = families(roots)
    if args.list:
        print_families(found)
        print(f"{len(found)} families")
        return 0
    current = Counter(label(key) for key, _ in found)
    if args.update:
        Path(BASELINE).write_text("".join(f"{name}\n" for name in sorted(current.elements())))
        print(f"duplication: baseline set to {len(found)} families")
        return 0
    baseline = read_baseline()
    added = current - baseline
    if added:
        print_families([(key, copies) for key, copies in found if label(key) in added])
        print(f"::error::{sum(added.values())} new method families are copied into {MIN_COPIES}+ files (above). Extract the "
              f"shared code instead. Only copies that must stay apart go in {BASELINE}, with the reason in the PR.")
        return 1
    gone = baseline - current
    if gone:
        print(f"::notice::{sum(gone.values())} method families are gone: {', '.join(sorted(gone.elements()))}. "
              "Drop them from the baseline: bash scripts/check-duplication.sh --update")
    print(f"duplication: {len(found)} method families, none beyond the baseline")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"::error::Duplication scan failed: {error}", file=sys.stderr)
        sys.exit(2)
