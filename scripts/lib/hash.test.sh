#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
source "$here/hash.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

fail=0
want=2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881
check() { # <label> <path>
  local got; got="$(sha256_of "$2")"
  if [ "$got" != "$want" ]; then echo "FAIL: $1 -> '$got' (want $want)"; fail=1; fi
}

printf 'x' > "$tmp/plain"
check "a plain path" "$tmp/plain"

printf 'x' > "$tmp/a\\b"
check "a path with a backslash" "$tmp/a\\b"

[ "$fail" -eq 0 ] && echo "ok" || exit 1
