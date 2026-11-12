#!/usr/bin/env bash
# Reads changed paths (one per line, repo-relative) on stdin and prints the test projects they can
# affect, one .csproj path per line, or the single word `all`.
#
# A path maps to the project whose directory holds it; every project that references that one,
# directly or transitively, is selected. TestSelectionDirectory items declare directories read by
# source-scanning tests without a project reference; their paths are relative to the .csproj.
# A path in no project directory (build files, workflows,
# scripts, the plugin) selects all, since nothing proves it is unread by a test. Markdown outside
# kcap/ is skipped: the workflow trigger already treats it as unable to affect a test.
#
# Usage: select-test-projects.sh [repo-root] < changed-paths
set -euo pipefail
set -f
root="${1:-.}"
cd "$root"

# Collapses `.` and `..` segments; bash 3.2 compatible, no realpath -m on macOS.
normalize() {
  local IFS=/ seg out=()
  for seg in $1; do
    case "$seg" in
      '' | .) ;;
      ..) [ "${#out[@]}" -gt 0 ] && out=("${out[@]:0:${#out[@]}-1}") ;;
      *) out+=("$seg") ;;
    esac
  done
  printf '%s\n' "${out[*]}"
}

projects="$(git ls-files '*.csproj')"
[ -n "$projects" ] || { echo "no .csproj files tracked under $root" >&2; exit 1; }

# One "dependent dependency" pair per line.
edges=""
inputs=""
while IFS= read -r proj; do
  dir="$(dirname "$proj")"
  refs="$(grep -oE '<ProjectReference[^>]*Include="[^"]+"' "$proj" | sed -E 's/.*Include="([^"]+)"/\1/' || true)"
  while IFS= read -r ref; do
    [ -n "$ref" ] || continue
    # A reference built from MSBuild properties cannot be resolved here.
    case "$ref" in *'$('*) echo all; exit 0 ;; esac
    edges+="$proj $(normalize "$dir/${ref//\\//}")"$'\n'
  done <<< "$refs"
  dirs="$(grep -oE '<TestSelectionDirectory[^>]*Include="[^"]+"' "$proj" | sed -E 's/.*Include="([^"]+)"/\1/' || true)"
  while IFS= read -r input; do
    [ -n "$input" ] || continue
    case "$input" in *'$('*) echo all; exit 0 ;; esac
    inputs+="$proj $(normalize "$dir/${input//\\//}")/"$'\n'
  done <<< "$dirs"
done <<< "$projects"

# Longest project directory that is a prefix of the path.
owner() {
  local path="$1" best="" proj dir found=""
  while IFS= read -r proj; do
    dir="$(dirname "$proj")/"
    case "$path" in
      "$dir"*) [ "${#dir}" -gt "${#best}" ] && best="$dir" && found="$proj" ;;
    esac
  done <<< "$projects"
  [ -n "$best" ] && printf '%s\n' "$found"
}

selected=""
while IFS= read -r path || [ -n "$path" ]; do
  path="${path%$'\r'}"
  [ -n "$path" ] || continue
  case "$path" in
    kcap/*) ;;
    *.md) continue ;;
  esac
  proj="$(owner "$path" || true)"
  if [ -z "$proj" ]; then echo all; exit 0; fi
  printf '%s\n' "$selected" | grep -Fxq -- "$proj" || selected+="$proj"$'\n'
  while read -r scanner directory; do
    [ -n "$scanner" ] || continue
    case "$path" in
      "$directory"*)
        printf '%s\n' "$selected" | grep -Fxq -- "$scanner" || selected+="$scanner"$'\n'
        ;;
    esac
  done <<< "$inputs"
done

# Reverse-dependency closure.
changed=1
while [ "$changed" -eq 1 ]; do
  changed=0
  while read -r from to; do
    [ -n "$from" ] || continue
    if printf '%s\n' "$selected" | grep -Fxq -- "$to" && ! printf '%s\n' "$selected" | grep -Fxq -- "$from"; then
      selected+="$from"$'\n'
      changed=1
    fi
  done <<< "$edges"
done

printf '%s' "$selected" | grep -E '\.Tests\.(Unit|Integration)\.csproj$' | sort || true
