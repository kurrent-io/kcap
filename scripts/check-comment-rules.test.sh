#!/usr/bin/env bash
# Each fixture under testdata/comment-rules marks every line the gate must report with HIT; every
# other line, string contents included, must pass. Fixtures end in .txt so the gate skips them here.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/check-comment-rules.sh"
fixtures="$here/testdata/comment-rules"

fail=0
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
n=0

new_repo() {
  n=$((n + 1)); repo="$scratch/r$n"
  mkdir -p "$repo" && git -C "$repo" init -q
}
run_gate() { out="$(cd "$repo" && bash "$sh" 2>&1)"; rc=$?; }

check_fixture() {
  local fixture="$1" path="$2" want got
  new_repo
  mkdir -p "$(dirname "$repo/$path")"
  cp "$fixtures/$fixture" "$repo/$path"
  git -C "$repo" add -A
  run_gate
  want="$(grep -n 'HIT' "$fixtures/$fixture" | cut -d: -f1 | tr '\n' ' ')"
  got="$(printf '%s\n' "$out" | grep "^$path:" | cut -d: -f2 | tr '\n' ' ')"
  if [ "$rc" != 1 ] || [ "$got" != "$want" ]; then
    echo "FAIL: $fixture -> rc=$rc, reported lines '$got' (want rc=1, lines '$want')"
    fail=1
  fi
}

check_fixture cs.txt src/A.cs
check_fixture sh.txt scripts/a.sh
check_fixture yml.txt .github/workflows/ci.yml
check_fixture axaml.txt src/V.axaml

new_repo
printf '%s\n' 'class A {}' '// a token the server no longer accepts' > "$repo/A.cs"
git -C "$repo" add -A
run_gate
[ "$rc" = 0 ] || { echo "FAIL: a clean repo -> rc=$rc (want 0)"; printf '%s\n' "$out" | sed 's/^/    /'; fail=1; }

[ "$fail" -eq 0 ] && echo "ok" || exit 1
