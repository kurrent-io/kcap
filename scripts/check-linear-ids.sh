#!/usr/bin/env bash
# Fails on a Linear issue id in tracked C#, XAML, MSBuild, shell or workflow files.
# Exit: 0 clean, 1 = violation found, 2 = checker failure (never "clean").
# POSIX ERE only, no PCRE or `grep -P`: macOS's BSD grep rejects -P outright.
set -u

# POSIX ERE has no \b: both guards reject an adjacent letter, digit or `_` and nothing else.
ID_RE='(^|[^A-Za-z0-9_])AI-[0-9]+($|[^A-Za-z0-9_])'
SUPPRESS_RE='(//|#|<!--)[[:space:]]*linear-id-ok:[[:space:]]*[^[:space:]]'
EXTS=(cs axaml csproj props targets sh yml yaml)

# Separate pathspec-scoped greps let git decide which tree a hit came from: reparsing one combined
# output for a path prefix fails open on C-quoted paths. `:(glob)` is required, since a plain
# `test/**/*.cs` misses files directly under test/.
strict=(); test=()
for e in "${EXTS[@]}"; do strict+=("*.$e"); test+=(":(glob)test/**/*.$e"); done

# git grep exits 0 = matches, 1 = none, >1 = error.
strict_matches="$(git grep --no-color -nE "$ID_RE" -- "${strict[@]}" ':(exclude)test/**')"
strict_rc=$?
[ "$strict_rc" -gt 1 ] && { echo "::error::git grep failed (exit $strict_rc) — treating as failure." >&2; exit 2; }

test_matches="$(git grep --no-color -nE "$ID_RE" -- "${test[@]}")"
test_rc=$?
[ "$test_rc" -gt 1 ] && { echo "::error::git grep failed (exit $test_rc) scanning test/ — treating as failure." >&2; exit 2; }

violations=""
[ "$strict_rc" -eq 0 ] && violations="$strict_matches"

# Under test/ only, a line carrying `linear-id-ok: <reason>` in a comment is synthetic id-shaped data.
if [ "$test_rc" -eq 0 ]; then
    test_violations="$(printf '%s' "$test_matches" | grep -vE "$SUPPRESS_RE")"
    tv_rc=$?
    [ "$tv_rc" -gt 1 ] && { echo "::error::grep failed (exit $tv_rc) applying the suppression filter — treating as failure." >&2; exit 2; }
    if [ "$tv_rc" -eq 0 ]; then
        violations="${violations:+$violations$'\n'}$test_violations"
    fi
fi

[ -z "$violations" ] && exit 0

echo "$violations"
echo "::error::Linear issue ID token found. Use the GitHub issue number instead. Suppression (linear-id-ok: <reason>) is for synthetic ID-shaped test data under test/ only; a hit anywhere else is always a violation."
exit 1
