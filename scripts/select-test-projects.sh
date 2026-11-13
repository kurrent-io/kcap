#!/usr/bin/env bash
# Reads changed paths (one per line, repo-relative) on stdin and prints the test projects they can
# affect, one .csproj path per line, or the single word `all`.
#
# A path maps to the project whose directory holds it; every project that references that one,
# directly or transitively, is selected. TestSelectionDirectory items declare directories read by
# source-scanning tests without a project reference; their paths are relative to the .csproj.
# A path in no project directory (build files, workflows, scripts, the plugin) selects all, since
# nothing proves it is unread by a test. The exception is markdown under kcap/ that a declared
# directory covers: markdown is never a build input, so the declaring suites are all it can reach.
# Anything that cannot be resolved here also selects all: a reference built from MSBuild
# properties, an item whose Include is not on its opening line, a declared directory that does not
# exist. Markdown outside kcap/ is skipped: the workflow trigger already treats it as unable to
# affect a test.
#
# Usage: select-test-projects.sh [repo-root] < changed-paths
set -euo pipefail
set -f
root="${1:-.}"
cd "$root"

# Read whole before anything can exit: a writer still blocked on the pipe when this script exits
# dies on SIGPIPE, which fails a caller running under pipefail.
changed="$(cat)"

all() { echo all; exit 0; }

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
  printf '%s\n' "${out[*]-}"
}

# includes <item> <project> — the item's Include values, one per line, a `;` list split up.
# Fails when it read fewer values than the project has such items.
includes() {
  local found
  found="$(grep -oE "<$1[^>]*Include=\"[^\"]+\"" "$2" || true)"
  [ "$(grep -o "<$1" "$2" | grep -c . || true)" -eq "$(printf '%s' "$found" | grep -c . || true)" ] || return 1
  printf '%s\n' "$found" | sed -E 's/.*Include="([^"]+)"/\1/' | tr ';' '\n'
}

projects="$(git ls-files '*.csproj')"
[ -n "$projects" ] || { echo "no .csproj files tracked under $root" >&2; exit 1; }

# edges: one "dependent dependency" pair per line. inputs: one "scanner directory/" pair per line,
# the directory empty for the repo root.
edges=""
inputs=""
while IFS= read -r proj; do
  dir="$(dirname "$proj")"
  refs="$(includes ProjectReference "$proj")" || all
  while IFS= read -r ref; do
    [ -n "$ref" ] || continue
    case "$ref" in *'$('*) all ;; esac
    edges+="$proj $(normalize "$dir/${ref//\\//}")"$'\n'
  done <<< "$refs"
  dirs="$(includes TestSelectionDirectory "$proj")" || all
  while IFS= read -r input; do
    [ -n "$input" ] || continue
    scanned="$(normalize "$dir/${input//\\//}")"
    [ -d "${scanned:-.}" ] || all
    inputs+="$proj ${scanned:+$scanned/}"$'\n'
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
while IFS= read -r path; do
  path="${path%$'\r'}"
  [ -n "$path" ] || continue
  case "$path" in
    kcap/*) ;;
    *.md) continue ;;
  esac
  proj="$(owner "$path" || true)"
  if [ -n "$proj" ]; then
    printf '%s\n' "$selected" | grep -Fxq -- "$proj" || selected+="$proj"$'\n'
  else
    case "$path" in *.md) ;; *) all ;; esac
  fi
  covered=""
  while read -r scanner directory; do
    [ -n "$scanner" ] || continue
    case "$path" in
      "$directory"*)
        covered=1
        printf '%s\n' "$selected" | grep -Fxq -- "$scanner" || selected+="$scanner"$'\n'
        ;;
    esac
  done <<< "$inputs"
  [ -n "$proj" ] || [ -n "$covered" ] || all
done <<< "$changed"

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
