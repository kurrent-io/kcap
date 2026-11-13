#!/usr/bin/env bash
# HIT marks exactly the lines each fixture must report; .txt keeps fixtures out of the gate.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
gate="$here/check-comment-rules.sh"
fixtures="$here/testdata/comment-rules"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
fail=0
n=0

new_repo() {
  n=$((n + 1)); repo="$scratch/r$n"
  mkdir -p "$repo"
  git -C "$repo" init -q
}
run_gate() {
  if out="$(cd "$repo" && bash "$gate" 2>&1)"; then rc=0; else rc=$?; fi
}
check() {
  local name="$1" expected="$2" snippet="${3:-}"
  run_gate
  if [ "$rc" != "$expected" ] || [[ "$out" != *"$snippet"* ]]; then
    printf 'FAIL: %s -> rc=%s (want %s), expected output containing %s\n%s\n' "$name" "$rc" "$expected" "$snippet" "$out"
    fail=1
  fi
}
check_fixture() {
  local fixture="$1" path="$2" want got
  new_repo
  mkdir -p "$(dirname "$repo/$path")"
  cp "$fixtures/$fixture" "$repo/$path"
  git -C "$repo" add -A
  run_gate
  want="$(grep -n 'HIT' "$fixtures/$fixture" | cut -d: -f1 | tr '\n' ' ')"
  got="$(printf '%s\n' "$out" | awk -F: -v path="$path" '$1 == path {print $2}' | tr '\n' ' ')"
  if [ "$rc" != 1 ] || [ "$got" != "$want" ]; then
    printf "FAIL: %s -> rc=%s, reported lines '%s' (want rc=1, lines '%s')\n%s\n" "$fixture" "$rc" "$got" "$want" "$out"
    fail=1
  fi
}

check_fixture cs.txt src/A.cs
check_fixture tokens.txt src/Tokens.cs
check_fixture sh.txt scripts/a.sh
check_fixture yml.txt .github/workflows/ci.yml
check_fixture yml.txt .github/actions/a/action.yaml
for ext in axaml csproj props targets; do
  check_fixture axaml.txt "src/V.$ext"
done

new_repo
check 'an empty repository' 0
printf '%s\n' '// Task 1' > "$repo/untracked.cs"
check 'untracked files are ignored' 0
printf '%s\n' 'class A {}' '// a token the server no longer accepts' > "$repo/A.cs"
git -C "$repo" add A.cs
check 'a clean tracked source' 0
printf '\357\273\277// Task 1\r\n// Task 2\r\n' > "$repo/A.cs"
check 'BOM and CRLF preserve lines' 1 'A.cs:2:'
mkdir "$repo/nested"
repo="$repo/nested"
check 'invocation below the root scans the repository' 1 'A.cs:2:'

new_repo
path='src/é space,%:file.cs'
mkdir -p "$repo/src"
printf '%s\n' '// Task 1' > "$repo/$path"
git -C "$repo" add -A
check 'special paths are read without shell splitting' 1 "$path:1:"
check 'annotation properties are escaped' 1 '::error file=src/é space%2C%25%3Afile.cs,line=1::'

new_repo
printf '%s\n' 'run: |' '  echo "# Task 1"' '  # Task 2' > "$repo/data.yaml"
git -C "$repo" add -A
check 'non-GitHub YAML block scalars are data' 0

new_repo
printf '%s\n' 'var s = """' '// Task 1' > "$repo/A.cs"
printf '%s\n' '// Task 2' > "$repo/B.cs"
git -C "$repo" add -A
check 'parser state does not cross files' 1 'B.cs:1:'

new_repo
printf '%s\n' 'cat <<EOF' 'data' 'EOF' > "$repo/a.sh"
git -C "$repo" add -A
check 'a heredoc closes at end of file' 0
printf '%s\n' 'cat <<EOF' 'data' > "$repo/a.sh"
check 'an unterminated heredoc is a checker failure' 2 'a.sh:'
printf '%s\n' 'echo "' > "$repo/a.sh"
check 'shell parser failures are not clean scans' 2 'a.sh:'
rm "$repo/a.sh"
check 'missing tracked files are checker failures' 2 'a.sh'

new_repo
printf '%s\n' '<Project>' > "$repo/a.props"
git -C "$repo" add -A
check 'XML parser failures are not clean scans' 2 'a.props:'

new_repo
printf '%s\n' 'name: "unterminated' > "$repo/a.yaml"
git -C "$repo" add -A
check 'YAML parser failures are not clean scans' 2 'a.yaml:'

new_repo
mkdir "$scratch/shim"
export COMMENT_RULES_TEST_GIT="$(command -v git)"
cat > "$scratch/shim/git" <<'SHIM'
#!/usr/bin/env bash
[ "$1" != ls-files ] || exit 42
exec "$COMMENT_RULES_TEST_GIT" "$@"
SHIM
chmod +x "$scratch/shim/git"
PATH="$scratch/shim:$PATH" check 'file enumeration failures are not clean scans' 2 'exit status 42'

repo="$scratch"
check 'invocation outside a repository is a checker failure' 2

[ "$fail" -eq 0 ] && echo 'ok' || exit 1
