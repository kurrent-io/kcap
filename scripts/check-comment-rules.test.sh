#!/usr/bin/env bash
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/check-comment-rules.sh"

fail=0
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
n=0

new_repo() {
  n=$((n + 1)); repo="$scratch/r$n"
  mkdir -p "$repo/scripts" && git -C "$repo" init -q
}
put() {
  mkdir -p "$(dirname "$repo/$1")"
  printf '%s\n' "$2" > "$repo/$1"
  git -C "$repo" add -A
}
check() {
  local name="$1" want_rc="$2" want_out="${3:-}"
  local out rc
  out="$(cd "$repo" && bash "$sh" 2>&1)"; rc=$?
  if [ "$rc" != "$want_rc" ] || { [ -n "$want_out" ] && [[ "$out" != *"$want_out"* ]]; }; then
    echo "FAIL: $name -> rc=$rc (want $want_rc${want_out:+, output containing '$want_out'})"
    printf '%s\n' "$out" | sed 's/^/    /'
    fail=1
  fi
}

new_repo
put src/A.cs $'class A\n{\n    // a token the server no longer accepts\n}'
check "present-tense 'no longer' passes" 0

new_repo
put src/A.cs $'class A\n{\n    // §4.2: the lane is serial\n}'
check "spec coordinate fails with file and line" 1 "src/A.cs:3:"

new_repo
put src/A.cs $'class A\n{\n    int x; // Qodo #1: clone the args\n}'
check "trailing bot-name comment fails with file and line" 1 "src/A.cs:3:"

new_repo
put src/A.cs $'// Bounded by RFC 8628 §3.4 and SemVer §10.\nclass A {}'
check "an external standard's section passes" 0

new_repo
put src/A.cs $'/*\n * Finding 2: must hold\n */\nclass A {}'
check "inside a block comment fails" 1 "src/A.cs:2:"

new_repo
put src/A.cs $'class A\n{\n    string g = "src/**/*.cs";\n    string t = "Task 1";\n}'
check "a glob in a string opens no comment" 0

new_repo
put src/A.cs $'class A\n{\n    string u = "https://x.test/Task 1";\n}'
check "a URL in a string opens no comment" 0

new_repo
put scripts/a.sh $'#!/usr/bin/env bash\n# Phase B: drain first\necho ok'
check "shell comment fails" 1 "scripts/a.sh:2:"

new_repo
put scripts/a.sh $'#!/usr/bin/env bash\necho "Phase B"'
check "shell code is not a comment" 0

new_repo
put .github/workflows/ci.yml "jobs:"$'\n'"  x: # round""-4 review fix"$'\n'"    runs-on: ubuntu-latest"
check "workflow comment fails" 1 ".github/workflows/ci.yml:2:"

new_repo
put src/V.axaml $'<UserControl>\n  <!--\n    Decision 3 keeps this\n  -->\n  <TextBlock Text="Task 1" />\n</UserControl>'
check "multi-line XAML comment fails, attribute text does not" 1 "src/V.axaml:3:"

new_repo
put src/V.axaml $'<UserControl>\n  <TextBlock Text="Task 1" />\n</UserControl>'
check "XAML attribute text passes" 0

new_repo
put src/A.cs $'/// The shape of a Qodo review: a finding inside quoted details.\nclass A {}'
check "a bot's name as a domain term passes" 0

new_repo
put src/A.cs $'class A\n{\n    // Regression for a Qodo-flagged false positive\n}'
check "a bot-flagged regression note fails" 1 "src/A.cs:3:"

new_repo
put src/A.cs $'class A\n{\n    string value = " // Task 1";\n}'
check "a quoted C# marker opens no comment" 0

new_repo
put src/A.cs $'class A\n{\n    char q = \'"\'; // Task 2\n}'
check "a char literal opens no string" 1 "src/A.cs:3:"

new_repo
put scripts/a.sh $'#!/usr/bin/env bash\necho " # Phase B" \' # Phase C\''
check "a quoted shell marker opens no comment" 0

new_repo
put src/A.cs $'class A\n{\n    int x;// Task 1\n}'
check "a line comment right after code fails" 1 "src/A.cs:3:"

new_repo
put src/A.cs $'class A\n{\n    int y;/* Task 2 */\n}'
check "a block comment right after code fails" 1 "src/A.cs:3:"

new_repo
put src/A.cs $'class A\n{\n    void F() {}// Task 3\n}'
check "a comment right after a closing brace fails" 1 "src/A.cs:3:"

new_repo
put src/A.cs $'// Parse Qodo (the HTML source).\nclass A {}'
check "a bot name before a parenthetical with no id passes" 0

new_repo
put src/A.cs $'// Regression for Qodo (#380, finding 3).\nclass A {}'
check "a bot name before a parenthesised id fails" 1 "src/A.cs:1:"

new_repo
put .github/workflows/ci.yml "jobs:"$'\n'"  x:"$'\n'"    name: Don't skip checks # Ta""sk 1"
check "an apostrophe in a YAML plain scalar opens no string" 1 ".github/workflows/ci.yml:3:"

new_repo
put .github/workflows/ci.yml "jobs:"$'\n'"  x:"$'\n'"    run: echo \"a # Ta""sk 1\" X='b # Ta""sk 2'"
check "a quoted string in a YAML run line opens no comment" 0

[ "$fail" -eq 0 ] && echo "ok" || exit 1
