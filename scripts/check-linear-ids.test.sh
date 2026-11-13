#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
gate="$here/check-linear-ids.sh"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
id="AI"-123
n=0
fail=0

check_file() {
  local path="$1" content="$2" expected="$3" out rc
  n=$((n + 1))
  local repo="$scratch/r$n"
  mkdir -p "$repo/$(dirname "$path")"
  git -C "$repo" init -q
  printf '%s\n' "$content" > "$repo/$path"
  git -C "$repo" add -A
  if out="$(cd "$repo" && bash "$gate" 2>&1)"; then rc=0; else rc=$?; fi
  if [ "$rc" != "$expected" ]; then
    printf 'FAIL: %s -> rc=%s (want %s)\n%s\n' "$path" "$rc" "$expected" "$out"
    fail=1
  fi
}

for ext in cs axaml csproj props targets sh yml yaml; do
  check_file "src/space dir/A.$ext" "// $id" 1
  check_file "root.$ext" "# $id" 1
  check_file "test/A.$ext" "// $id" 1
  for marker in // '#' '<!--'; do
    check_file "test/é space/A.$ext" "$marker $id $marker linear-id-ok: synthetic input" 0
    check_file "src/A.$ext" "$marker $id $marker linear-id-ok: synthetic input" 1
  done
  check_file "test/A.$ext" "// $id // linear-id-ok: " 1
  check_file "src/A.$ext" "PREFIX_$id $id"'B' 0
  check_file "testish/A.$ext" "// $id // linear-id-ok: synthetic input" 1
done
check_file 'docs/data.txt' "$id" 0
[ "$fail" -eq 0 ] && echo 'ok' || exit 1
