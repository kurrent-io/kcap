#!/usr/bin/env python3
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from xml.parsers import expat

try:
    from tree_sitter import Language, Parser, Query, QueryCursor
    import tree_sitter_c_sharp
    import tree_sitter_yaml
except ImportError:
    print("comment rules: install scripts/comment-rules-requirements.txt in a Python 3.10+ virtual environment", file=sys.stderr)
    sys.exit(2)


TOKEN = re.compile(
    r"§[ \t]?[0-9]"
    r"|\b(?:Task|Finding|Decision) [0-9]+\b"
    r"|\bPhase [A-D0-9]\b"
    r"|\b(?:Qodo|Bugbot|CodeRabbit)(?: ?#[0-9]+\b"
    r"| ?\((?:#[0-9]+|PR[ \t]+#?[0-9]+|[0-9]+)\b"
    r"| PR[ \t]+#?[0-9]+\b| P[0-9]\b| (?:review on|finding|fix|bug|regression)\b|[- ]flagged\b)"
    r"|\b[Rr]ound-?[0-9]+ (?:review|fix|finding)\b"
    r"|\b[Pp]re-fix\b"
    r"|\b[Aa]n earlier (?:revision|version|draft)\b"
    r"|\bBLOCKER-?[0-9]",
    re.ASCII,
)
STANDARD = re.compile(r"\b(?:RFC[ \t]+[0-9]+|SemVer)[ \t,]*§")
EXTENSIONS = ("cs", "axaml", "csproj", "props", "targets", "sh", "yml", "yaml")
PARSERS = {
    name: Parser(Language(module.language()))
    for name, module in (
        ("cs", tree_sitter_c_sharp),
        ("yaml", tree_sitter_yaml),
    )
}
COMMENTS = {name: Query(parser.language, "(comment) @comment") for name, parser in PARSERS.items()}
SCRIPTS = Query(PARSERS["yaml"].language, """
(block_mapping_pair
  key: (_) @steps
  value: (block_node (block_sequence (block_sequence_item (block_node (block_mapping
    (block_mapping_pair key: (_) @key value: (block_node (block_scalar) @script))))))))
""")


def shell_comments(source, workflow=False):
    # Template expressions are substituted before Bash reads a workflow; keep their source offsets.
    expression = rb"\$\{\{(?:'(?:[^']|'')*'|[^'}]|}(?!}))*}}"
    parsed = re.sub(expression, lambda match: re.sub(rb"[^\r\n]", b"x", match[0]), source) if workflow else source
    process = subprocess.run([str(Path(sys.executable).with_name("shfmt")), "-ln=bash", "--to-json"],
                             input=parsed, capture_output=True)
    if process.returncode:
        raise ValueError(process.stderr.decode("utf-8").strip())
    pending = [json.loads(process.stdout)]
    while pending:
        node = pending.pop()
        if isinstance(node, dict):
            if "Hash" in node and "Text" in node:
                yield node["Hash"]["Line"], source[node["Hash"]["Offset"] + 1:node["End"]["Offset"]].decode("utf-8")
            else:
                pending.extend(node.values())
        elif isinstance(node, list):
            pending.extend(node)


def mapping_value(node, key):
    while node is not None and node.type in ("block_node", "flow_node"):
        node = next((child for child in node.named_children if child.type != "comment"), None)
    if node is not None:
        for pair in node.named_children:
            if pair.type == "block_mapping_pair":
                name = pair.child_by_field_name("key")
                if name is not None and name.text.strip(b"'\"") == key:
                    return pair.child_by_field_name("value")
    return None


def runner_shell(runner):
    if runner is None:
        return b""
    labels = mapping_value(runner, b"labels")
    if labels is not None:
        runner = labels
    elif mapping_value(runner, b"group") is not None:
        return b""
    if b"${{" in runner.text:
        return b""
    labels = re.findall(rb"[a-z0-9_.-]+", runner.text.lower())
    if any(label in (b"linux", b"macos") or label.startswith((b"ubuntu-", b"macos-", b"xcode-"))
           for label in labels):
        return b"bash"
    return b""


def run_shell(pair):
    scope = pair.parent
    shell = mapping_value(scope, b"shell")
    runner = None
    while shell is None and scope is not None:
        if scope.type == "block_mapping":
            if runner is None:
                runner = mapping_value(scope, b"runs-on")
            defaults = mapping_value(scope, b"defaults")
            shell = mapping_value(mapping_value(defaults, b"run"), b"shell")
        scope = scope.parent
    return shell.text.strip(b"'\"") if shell is not None else runner_shell(runner)


def syntax_comments(source, language, github=False):
    # Error recovery lets a grammar read comments in source using newer language constructs.
    root = PARSERS[language].parse(source).root_node
    if language == "yaml" and root.has_error:
        raise ValueError("cannot parse YAML")
    for node in QueryCursor(COMMENTS[language]).captures(root).get("comment", []):
        yield node.start_point.row + 1, node.text.decode("utf-8")
    if language == "yaml" and github:
        for _, captures in QueryCursor(SCRIPTS).matches(root):
            key, script = captures["key"][0], captures["script"][0]
            if captures["steps"][0].text.strip(b"'\"") != b"steps":
                continue
            if key.text.strip(b"'\"") != b"run" or not script.text.startswith(b"|"):
                continue
            # Only literal Bash/sh scripts retain the source's line structure and comment syntax.
            shell = run_shell(key.parent)
            if not shell.split() or shell.split()[0] not in (b"bash", b"sh"):
                continue
            header, body = script.text.split(b"\n", 1) if b"\n" in script.text else (script.text, b"")
            explicit = re.match(rb"\|[-+]?([1-9])", header)
            lines = body.split(b"\n")
            indent = (key.start_point.column + int(explicit[1])) if explicit else next(
                (len(line) - len(line.lstrip(b" ")) for line in lines if line.strip()), 0)
            body = b"\n".join(line[indent:] if line.strip() else b"" for line in lines)
            for row, comment in shell_comments(body, workflow=True):
                yield script.start_point.row + row + 1, comment


def xml_comments(source):
    parser = expat.ParserCreate()
    comments = []
    parser.CommentHandler = lambda text: comments.append((parser.CurrentLineNumber, text))
    parser.Parse(source, True)
    return comments


def violation_rows(source, extension, github=False):
    if extension == "sh":
        comments = shell_comments(source)
    elif extension == "cs":
        comments = syntax_comments(source, "cs")
    elif extension in ("yml", "yaml"):
        comments = syntax_comments(source, "yaml", github)
    else:
        comments = xml_comments(source)
    return sorted({start + offset
                   for start, text in comments
                   for offset, line in enumerate(text.split("\n"))
                   if TOKEN.search(STANDARD.sub("", line))})


def annotation_escape(text, property_value=False):
    text = text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    return text.replace(":", "%3A").replace(",", "%2C") if property_value else text


def main():
    root = subprocess.check_output(["git", "rev-parse", "--show-toplevel"]).decode().rstrip("\n")
    os.chdir(root)
    paths = subprocess.check_output(["git", "ls-files", "-z", "--", *(f"*.{ext}" for ext in EXTENSIONS)])
    found = False
    for raw_path in paths.split(b"\0"):
        if not raw_path:
            continue
        path = os.fsdecode(raw_path)
        source = Path(path).read_bytes()
        lines = source.decode("utf-8-sig").split("\n")
        try:
            rows = violation_rows(source, path.rsplit(".", 1)[1], path.startswith(".github/"))
        except Exception as error:
            raise ValueError(f"{path}: {error}") from error
        for row in rows:
            found = True
            print(f"{path}:{row}: {lines[row - 1].strip()}")
            print(f"::error file={annotation_escape(path, True)},line={row}::Banned comment token. "
                  "State the constraint without the spec coordinate, review artifact or change narration (CLAUDE.md, Comments).")
    return int(found)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"::error::Comment scan failed: {annotation_escape(str(error))}", file=sys.stderr)
        sys.exit(2)
