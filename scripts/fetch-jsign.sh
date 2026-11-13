#!/usr/bin/env bash
# Downloads the pinned jsign into <dir> and prints the jar's path, refusing any other bytes.
# Usage: fetch-jsign.sh <dir>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"
version=7.6
pin=6cf58aa080e7ff82656f320cff8d4a3bff30408df8766806dcba3740dd839fa5
dir="${1:?usage: fetch-jsign.sh <dir>}"

mkdir -p "$dir"
jar="$dir/jsign-$version.jar"
curl -fsSL --retry 3 -o "$jar" "https://repo1.maven.org/maven2/net/jsign/jsign/$version/jsign-$version.jar"
actual="$(sha256_of "$jar")"
if [ "$actual" != "$pin" ]; then
  rm -f "$jar"
  echo "::error::jsign-$version.jar hashes to $actual, pinned $pin" >&2
  exit 1
fi
printf '%s\n' "$jar"
