#!/usr/bin/env bash
# Usage: bundle-app-plugin.sh <resources-or-app-directory> <version>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
destination="${1:?destination required}"
version="${2:?version required}"
[[ "$version" =~ ^[0-9A-Za-z.+-]+$ ]] || { echo "invalid version" >&2; exit 1; }
mkdir -p "$destination"
# Git Bash on Windows resolves "kcap" to a sibling kcap.exe in existence tests and in cp's
# target check; mkdir takes the name literally and still refuses an existing plugin.
mkdir "$destination/kcap"
cp -R "$here/../kcap/." "$destination/kcap/"
# shellcheck source=lib/plugin-manifests.sh
source "$here/lib/plugin-manifests.sh"
for manifest in "${plugin_manifests[@]}"; do
  sed -i.bak -E 's/"version": *"[^"]*"/"version": "'"${version%%+*}"'"/' "$destination/kcap/$manifest"
  rm "$destination/kcap/$manifest.bak"
done
