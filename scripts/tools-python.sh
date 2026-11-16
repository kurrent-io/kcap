#!/usr/bin/env bash
# Prints a Python 3.10+ with exactly the versions pinned in comment-rules-requirements.txt. The python3 on PATH is used
# when it already has them, as in CI; otherwise each pin set gets venvs under the clone's git directory, shared by its
# worktrees. A venv is published by rename once complete and never deleted, so a caller still running from one is
# never pulled out from under; one that stops working (its base interpreter removed) is passed over for a new one.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)" || exit 2
requirements="$here/comment-rules-requirements.txt"
pinned='
import sys
from importlib import metadata
if sys.version_info < (3, 10):
    sys.exit(1)
for line in open(sys.argv[1]):
    name, _, version = line.split("#")[0].strip().partition("==")
    if name:
        try:
            if metadata.version(name) != version:
                sys.exit(1)
        except metadata.PackageNotFoundError:
            sys.exit(1)
'

if command -v python3 >/dev/null && python3 -c "$pinned" "$requirements" 2>/dev/null; then
  command -v python3
  exit 0
fi

common="$(git -C "$here" rev-parse --path-format=absolute --git-common-dir 2>/dev/null)" || {
  echo 'tools: not inside a git checkout' >&2
  exit 2
}
prefix="$common/kcap-tools-venv-$(cksum <"$requirements" | cut -d' ' -f1)"
for published in "$prefix" "$prefix"-*; do
  if [ -x "$published/bin/python3" ] && "$published/bin/python3" -c "$pinned" "$requirements" 2>/dev/null; then
    echo "$published/bin/python3"
    exit 0
  fi
done
# The first build takes the plain name; a later one, after every published venv stopped working, a fresh one.
venv="$prefix"
[ -e "$venv" ] && venv="$prefix-$(date +%s)-$$"

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

echo "tools: building $venv from $base" >&2
building="$(mktemp -d "$common/kcap-tools-build.XXXXXX")" || exit 2
if "$base" -m venv "$building" >&2 &&
  "$building/bin/python3" -m pip install --quiet --only-binary=:all: -r "$requirements" >&2; then
  # rename(2) refuses a target that exists and is not empty, so a racing caller that published first wins and this
  # build is dropped.
  "$base" -c 'import os, sys; os.rename(sys.argv[1], sys.argv[2])' "$building" "$venv" 2>/dev/null || rm -rf "$building"
else
  rm -rf "$building"
fi
"$venv/bin/python3" -c "$pinned" "$requirements" 2>/dev/null || {
  echo "tools: could not install $requirements into $venv" >&2
  exit 2
}
echo "$venv/bin/python3"
