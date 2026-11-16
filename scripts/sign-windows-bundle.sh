#!/usr/bin/env bash
# Authenticode-signs every PE image in an extracted Windows local-edition bundle that carries no
# signature yet. A signed one, such as Microsoft's runtime DLLs, keeps only its publisher's: jsign
# would add kcap's beside it rather than skip it.
# Usage: sign-windows-bundle.sh <bundle-dir>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/pe.sh
source "$here/lib/pe.sh"
dir="${1:?usage: sign-windows-bundle.sh <bundle-dir>}"
dir="${dir%/}"

[ -f "$dir/kcap-server.exe" ] || { echo "$dir/kcap-server.exe is missing" >&2; exit 1; }

total=0; unsigned=()
while IFS= read -r f; do
  total=$(( total + 1 ))
  pe_signed "$f" || unsigned+=("$f")
done < <(pe_files "$dir")

if [ "${#unsigned[@]}" -gt 0 ]; then bash "$here/sign-windows.sh" "${unsigned[@]}"; fi
echo "signed ${#unsigned[@]} of $total PE files; the others already carried a signature" >&2
