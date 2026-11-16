#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
gate="$here/check-duplication.sh"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
fail=0
n=0

body='var total = 0; foreach (var item in items) { if (item.Length > limit) { total += item.Length; } } Console.WriteLine($"total {total} over {limit}"); return total;'
other='using var stream = File.OpenRead(path); var buffer = new byte[4096]; var read = stream.Read(buffer, 0, buffer.Length); Console.Error.WriteLine(read); return read * 2;'

new_repo() {
  n=$((n + 1)); repo="$scratch/r$n"
  mkdir -p "$repo/scripts"
  git -C "$repo" init -q
}
method() {
  local path="$1" text="$2" params="${3:-string[] items, int limit}"
  mkdir -p "$(dirname "$repo/$path")"
  printf 'class C\n{\n    // %s\n    static int Sum(%s)\n    {\n        %s\n    }\n}\n' "$path" "$params" "$text" > "$repo/$path"
  git -C "$repo" add -A
}
baseline() { printf '%s\n' "$1" > "$repo/scripts/check-duplication.baseline"; }
check() {
  local name="$1" expected="$2" snippet="$3"; shift 3
  if out="$(cd "$repo" && bash "$gate" "$@" 2>&1)"; then rc=0; else rc=$?; fi
  if [ "$rc" != "$expected" ] || [[ "$out" != *"$snippet"* ]]; then
    printf 'FAIL: %s -> rc=%s (want %s), expected output containing %s\n%s\n' "$name" "$rc" "$expected" "$snippet" "$out"
    fail=1
  fi
}

new_repo
method src/A/One.cs "$body"; method src/B/Two.cs "$body"; method src/C/Three.cs "$body"
baseline 'Sum(string[], int)'
check 'a family the baseline lists' 0 '1 method families, none beyond the baseline'
baseline ''
check 'a family the baseline does not list' 1 'src/C/Three.cs:4'
baseline 'Other(int)'
check 'swapping one family for another' 1 '1 new method families'
baseline "$(printf 'Gone(int)\nSum(string[], int)')"
check 'a family gone passes and says so' 0 'families are gone: Gone(int)'
check 'update drops a gone family' 0 'baseline set to 1 families' --update
check 'the updated baseline holds' 0 'none beyond the baseline'
[ "$(cat "$repo/scripts/check-duplication.baseline")" = 'Sum(string[], int)' ] || { echo 'FAIL: update writes the family names'; fail=1; }

new_repo
method src/A/One.cs "$body"; method src/B/Two.cs "$body"
baseline ''
check 'two copies are not a family' 0 'none beyond the baseline'
method src/B/Other.cs "$body"
check 'a third file makes a family' 1 'x3'
git -C "$repo" rm -q -f src/B/Other.cs
method test/T/Three.cs "$body"
check 'copies outside src are ignored' 0 'none beyond the baseline'
cp "$repo/src/A/One.cs" "$repo/src/B/Untracked.cs"
check 'untracked copies are ignored' 0 'none beyond the baseline'

new_repo
method src/A/One.cs "$body"; method src/B/Two.cs "$body"; method src/C/Three.cs "$other"
baseline ''
check 'a dissimilar third body' 0 'none beyond the baseline'
method src/D/Four.cs "${body/limit)/limit + 1)}"
check 'a near-identical third body' 1 'x3'

new_repo
for f in A B C; do method "src/$f/Short.cs" 'return items.Length + limit;'; done
baseline ''
check 'one-line bodies are not a family' 0 'none beyond the baseline'

new_repo
spaces="$(printf '%120s' '')"
for f in A B C; do method "src/$f/Padded.cs" "return \"$spaces\" + limit;"; done
baseline ''
check 'whitespace inside a literal is not body size' 0 'none beyond the baseline'

new_repo
interpolated='return $"the running total of every single item measured over the configured length limit {limit COMMENT} is {items.Sum(i => i.Length)} across {items.Length} items";'
for f in A B C; do method "src/$f/Note.cs" "${interpolated/COMMENT//* $f */}"; done
baseline ''
check 'a comment inside an interpolation is not code' 1 'x3'

new_repo
variadic='int limit, params string[] items'
method src/A/One.cs "$body" "$variadic"; method src/B/Two.cs "$body" "$variadic"; method src/C/Three.cs "$body" 'int limit, string[] items'
baseline ''
check 'a params array is part of the signature' 0 'none beyond the baseline'
method src/D/Four.cs "$body" "$variadic"
check 'three params copies are a family' 1 'Sum(int, params string[]) x3'

new_repo
mkdir "$repo/src"
for f in A B; do
  { printf 'namespace N;\nusing System;\nclass C\n{\n    void M()\n    {\n'
    for i in 1 2 3 4 5 6 7 8 9 10; do printf '        using var s%s = Open("%s");\n' "$i" "$i"; done
    printf '    }\n}\n'; } > "$repo/src/$f.cs"
done
git -C "$repo" add -A
check 'using statements are code, using directives are not' 0 'src                                                  32          24' --clones

new_repo
method src/A/One.cs "$body"
check 'a missing baseline is a checker failure' 2 'Duplication scan failed'
printf 'class C { void M() { int x = 1 } }\n' > "$repo/src/A/Broken.cs"
baseline ''
git -C "$repo" add -A
check 'a syntax error does not stop the scan' 0 'none beyond the baseline'

[ "$fail" -eq 0 ] && echo 'ok' || exit 1
