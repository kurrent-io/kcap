#!/usr/bin/env bash
# Prints a Python 3.10+ that has the pinned parsers in comment-rules-requirements.txt. The python3 on PATH is used
# when it already has them, as in CI; otherwise one venv per clone, shared by its worktrees, is built and reused
# until the pins change.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)" || exit 2
requirements="$here/comment-rules-requirements.txt"
probe='import sys, tree_sitter, tree_sitter_c_sharp, tree_sitter_yaml; sys.exit(sys.version_info < (3, 10))'

if command -v python3 >/dev/null && python3 -c "$probe" 2>/dev/null; then
  command -v python3
  exit 0
fi

common="$(git -C "$here" rev-parse --path-format=absolute --git-common-dir 2>/dev/null)" || {
  echo 'tools: not inside a git checkout' >&2
  exit 2
}
venv="$common/kcap-tools-venv"
pins="$(cksum <"$requirements")"
if [ -x "$venv/bin/python3" ] && [ "$(cat "$venv/pins" 2>/dev/null)" = "$pins" ]; then
  echo "$venv/bin/python3"
  exit 0
fi

base=""
for candidate in python3.13 python3.12 python3.11 python3.10 python3; do
  if command -v "$candidate" >/dev/null && "$candidate" -c 'import sys; sys.exit(sys.version_info < (3, 10))' 2>/dev/null; then
    base="$(command -v "$candidate")"
    break
  fi
done
[ -n "$base" ] || {
  echo 'tools: Python 3.10+ is required (python3.10 to python3.13, or a python3 of that version, on PATH)' >&2
  exit 2
}

echo "tools: building $venv from $base (once per pin change)" >&2
rm -rf "$venv"
"$base" -m venv "$venv" >&2 &&
  "$venv/bin/python3" -m pip install --quiet --only-binary=:all: -r "$requirements" >&2 &&
  echo "$pins" >"$venv/pins" || {
  echo "tools: could not install $requirements into $venv" >&2
  rm -rf "$venv"
  exit 2
}
echo "$venv/bin/python3"
