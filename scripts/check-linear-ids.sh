#!/usr/bin/env bash
# Fails on a Linear issue id in tracked C# source.
# Exit: 0 clean, 1 = violation found, 2 = checker failure (never "clean").
# POSIX ERE only, no PCRE or `grep -P`: macOS's BSD grep rejects -P outright.
set -u

# POSIX ERE has no \b: both guards reject an adjacent letter, digit or `_` and
# nothing else, so a hyphen-preceded id still matches.
ID_RE='(^|[^A-Za-z0-9_])AI-[0-9]+($|[^A-Za-z0-9_])'
SUPPRESS_RE='//[[:space:]]*linear-id-ok:[[:space:]]*[^[:space:]]'

# src/ and test/ are scanned by separate pathspec-scoped greps so git decides
# which tree a hit came from. Reparsing one combined output for a `src/` prefix
# fails open: git C-quotes paths with spaces or non-ASCII bytes, and colour
# config can inject escapes. `:(glob)` is required, since a plain `src/**/*.cs`
# pathspec misses files directly under src/.
# git grep exits 0 = matches, 1 = none, >1 = error; an error exits 2.

src_matches="$(git grep --no-color -nE "$ID_RE" -- ':(glob)src/**/*.cs')"
src_rc=$?
[ "$src_rc" -gt 1 ] && { echo "::error::git grep failed (exit $src_rc) scanning src/ — treating as failure." >&2; exit 2; }

test_matches="$(git grep --no-color -nE "$ID_RE" -- ':(glob)test/**/*.cs')"
test_rc=$?
[ "$test_rc" -gt 1 ] && { echo "::error::git grep failed (exit $test_rc) scanning test/ — treating as failure." >&2; exit 2; }

# src/ hits are always violations: suppression never applies there.
src_violations=""
[ "$src_rc" -eq 0 ] && src_violations="$src_matches"

# test/ hits are dropped when the line carries `// linear-id-ok: <reason>`.
# The filter runs only on a non-empty match set, so grep -v exiting 1 means
# every hit was suppressed.
test_violations=""
if [ "$test_rc" -eq 0 ]; then
    test_violations="$(printf '%s' "$test_matches" | grep -vE "$SUPPRESS_RE")"
    tv_rc=$?
    [ "$tv_rc" -gt 1 ] && { echo "::error::grep failed (exit $tv_rc) applying the suppression filter to test/ matches — treating as failure." >&2; exit 2; }
    [ "$tv_rc" -eq 1 ] && test_violations=""  # every test/ hit was correctly suppressed
fi

if [ -n "$src_violations" ] && [ -n "$test_violations" ]; then
    violations="$(printf '%s\n%s' "$src_violations" "$test_violations")"
elif [ -n "$src_violations" ]; then
    violations="$src_violations"
else
    violations="$test_violations"
fi

[ -z "$violations" ] && exit 0

echo "$violations"
echo "::error::Linear issue ID token found in tracked C# source. Rewrite per docs/superpowers/specs/2026-07-17-ai1392-linear-id-comment-sweep-design.md. Suppression (// linear-id-ok: <reason>) is for synthetic ID-shaped test data under test/ ONLY — any hit under src/ is always a violation regardless of the marker, and a genuine internal reference must be escalated to an owner before merge, never suppressed."
exit 1
