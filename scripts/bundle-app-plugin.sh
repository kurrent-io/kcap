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
# Without Git Bash, Claude Code runs a shell-form hook through PowerShell, whose start-up can outlast
# the hook's timeout. A bundle that ships kcap.exe gives Claude the hooks that start it directly.
if [ -f "$destination/bin/kcap.exe" ] || [ -f "$destination/kcap.exe" ]; then
  cp "$destination/kcap/hooks/hooks.windows.json" "$destination/kcap/hooks/hooks.json"
fi
manifest="$destination/kcap/.claude-plugin/plugin.json"
sed -i.bak -E 's/"version": *"[^"]*"/"version": "'"${version%%+*}"'"/' "$manifest"
rm "$manifest.bak"
