#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/select-test-projects.sh"

repo="$(mktemp -d)"
trap 'rm -rf "$repo"' EXIT

# csproj <path> [reference...] — references are written Windows-style, as the real projects are.
csproj() {
  local path="$1"; shift
  mkdir -p "$repo/$(dirname "$path")"
  {
    echo '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>'
    for ref in "$@"; do echo "  <ProjectReference Include=\"${ref//\//\\}\" />"; done
    echo '</ItemGroup></Project>'
  } > "$repo/$path"
}
csproj src/Core/Core.csproj
csproj src/Daemon/Daemon.csproj ../Core/Core.csproj
csproj src/App/App.csproj ../Core/Core.csproj
csproj src/Service/Service.csproj
csproj test/Helpers/Helpers.csproj ../../src/Core/Core.csproj
csproj test/Daemon.Tests.Unit.Host/Daemon.Tests.Unit.Host.csproj ../../src/Daemon/Daemon.csproj
csproj test/Daemon.Tests.Unit/Daemon.Tests.Unit.csproj ../Helpers/Helpers.csproj ../../src/Daemon/Daemon.csproj ../Daemon.Tests.Unit.Host/Daemon.Tests.Unit.Host.csproj
csproj test/App.Tests.Unit/App.Tests.Unit.csproj ../Helpers/Helpers.csproj ../../src/App/App.csproj
csproj test/Service.Tests.Unit/Service.Tests.Unit.csproj ../../src/Service/Service.csproj
csproj test/Cli.Tests.Integration/Cli.Tests.Integration.csproj ../Helpers/Helpers.csproj ../../src/Daemon/Daemon.csproj
git -C "$repo" init -q
git -C "$repo" add -A

fail=0
assert() {
  local name="$1" input="$2" want="$3" got
  got="$(printf '%b' "$input" | bash "$sh" "$repo" | tr '\n' ' ' | sed 's/ $//')"
  if [ "$got" != "$want" ]; then echo "FAIL: $name -> '$got' (want '$want')"; fail=1; fi
}

daemon_tests='test/Cli.Tests.Integration/Cli.Tests.Integration.csproj test/Daemon.Tests.Unit/Daemon.Tests.Unit.csproj'
all_core='test/App.Tests.Unit/App.Tests.Unit.csproj test/Cli.Tests.Integration/Cli.Tests.Integration.csproj test/Daemon.Tests.Unit/Daemon.Tests.Unit.csproj'

assert "app only"                 'src/App/Views/Main.cs'                   'test/App.Tests.Unit/App.Tests.Unit.csproj'
assert "leaf with no dependents"  'src/Service/Svc.cs'                      'test/Service.Tests.Unit/Service.Tests.Unit.csproj'
assert "transitive dependents"    'src/Daemon/Runner.cs'                    "$daemon_tests"
assert "shared library"           'src/Core/Models.cs'                      "$all_core"
assert "test helpers"             'test/Helpers/TempDir.cs'                 "$all_core"
assert "test project itself"      'test/App.Tests.Unit/HomeTests.cs'        'test/App.Tests.Unit/App.Tests.Unit.csproj'
assert "longest prefix wins"      'test/Daemon.Tests.Unit.Host/Program.cs'  'test/Daemon.Tests.Unit/Daemon.Tests.Unit.csproj'
assert "union of two changes"     'src/App/A.cs\nsrc/Service/B.cs'          'test/App.Tests.Unit/App.Tests.Unit.csproj test/Service.Tests.Unit/Service.Tests.Unit.csproj'
assert "outside any project"      'src/App/A.cs\n.github/workflows/ci.yml'  'all'
assert "build file at the root"   'Directory.Packages.props'                'all'
assert "test-wide config"         'test/testconfig.json'                    'all'
assert "markdown is skipped"      'docs/guide.md\nsrc/App/A.cs'             'test/App.Tests.Unit/App.Tests.Unit.csproj'
assert "only markdown"            'README.md'                               ''
assert "plugin markdown counts"   'kcap/skills/x/SKILL.md'                  'all'
assert "CRLF input"               'src/App/A.cs\r'                          'test/App.Tests.Unit/App.Tests.Unit.csproj'

# More input than a pipe holds, with the path that selects all on the first line: the writer is
# still blocked when the answer is known, and must not be left to die on SIGPIPE.
big="$(echo .github/workflows/ci.yml; i=0; while [ "$i" -lt 2000 ]; do echo "src/App/Views/Generated/Fixture$i.cs"; i=$((i + 1)); done)"
if ! printf '%s\n' "$big" | bash "$sh" "$repo" > /dev/null; then echo "FAIL: writer killed by an early exit"; fail=1; fi

# scans <project> <item xml> — gives a test project its scanned-directory declaration.
scans() {
  mkdir -p "$repo/test/$1"
  printf '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>\n%s\n</ItemGroup></Project>\n' "$2" > "$repo/test/$1/$1.csproj"
}
mkdir -p "$repo/src/App/View"
scans SourceGuards.Tests.Unit '<TestSelectionDirectory Include="..\..\src\" />'
scans TestGuards.Tests.Unit   '<TestSelectionDirectory Include="../../test" />'
scans PrefixGuards.Tests.Unit '<TestSelectionDirectory Include="../../src/App/View" />'
git -C "$repo" add -A

assert "source scanner without a reference" 'src/App/A.cs' 'test/App.Tests.Unit/App.Tests.Unit.csproj test/SourceGuards.Tests.Unit/SourceGuards.Tests.Unit.csproj'
assert "source scanner and transitive dependents" 'src/Daemon/A.cs' "$daemon_tests test/SourceGuards.Tests.Unit/SourceGuards.Tests.Unit.csproj"
assert "scanner covers added or deleted source" 'src/App/New.cs\nsrc/App/Deleted.cs' 'test/App.Tests.Unit/App.Tests.Unit.csproj test/SourceGuards.Tests.Unit/SourceGuards.Tests.Unit.csproj'
assert "test scanner without a reference" 'test/App.Tests.Unit/A.cs' 'test/App.Tests.Unit/App.Tests.Unit.csproj test/TestGuards.Tests.Unit/TestGuards.Tests.Unit.csproj'
assert "scanner does not match a sibling prefix" 'src/App/Views/Main.cs' 'test/App.Tests.Unit/App.Tests.Unit.csproj test/SourceGuards.Tests.Unit/SourceGuards.Tests.Unit.csproj'
assert "nested scanner directory" 'src/App/View/Main.cs' 'test/App.Tests.Unit/App.Tests.Unit.csproj test/PrefixGuards.Tests.Unit/PrefixGuards.Tests.Unit.csproj test/SourceGuards.Tests.Unit/SourceGuards.Tests.Unit.csproj'
assert "scanner preserves markdown skip" 'src/App/README.md' ''
assert "scanner preserves global fallback" 'Directory.Build.props' 'all'

scans PrefixGuards.Tests.Unit '<TestSelectionDirectory Include="../../src/Service;../../src/Daemon" />'
assert "scanner list in one Include" 'src/Service/Svc.cs' 'test/PrefixGuards.Tests.Unit/PrefixGuards.Tests.Unit.csproj test/Service.Tests.Unit/Service.Tests.Unit.csproj test/SourceGuards.Tests.Unit/SourceGuards.Tests.Unit.csproj'
scans PrefixGuards.Tests.Unit '<TestSelectionDirectory Include="../.." />'
assert "scanner of the whole repo" 'test/App.Tests.Unit/A.cs' 'test/App.Tests.Unit/App.Tests.Unit.csproj test/PrefixGuards.Tests.Unit/PrefixGuards.Tests.Unit.csproj test/TestGuards.Tests.Unit/TestGuards.Tests.Unit.csproj'

# A declaration that would match nothing must not quietly unselect its suite.
scans PrefixGuards.Tests.Unit '<TestSelectionDirectory Include="$(RepoRoot)/src/App/View" />'
assert "scanner directory from a property" 'src/App/A.cs' 'all'
scans PrefixGuards.Tests.Unit '<TestSelectionDirectory Include="../../src/**/*.cs" />'
assert "scanner glob" 'src/App/A.cs' 'all'
scans PrefixGuards.Tests.Unit '<TestSelectionDirectory Include="../../src/Gone" />'
assert "scanner directory that does not exist" 'src/App/A.cs' 'all'
scans PrefixGuards.Tests.Unit "$(printf '<TestSelectionDirectory\n    Include="../../src" />')"
assert "scanner Include on its own line" 'src/App/A.cs' 'all'
scans PrefixGuards.Tests.Unit '<TestSelectionDirectory Include="../../src/App/View" />'

csproj src/Odd/Odd.csproj '$(RepoRoot)/src/Core/Core.csproj'
git -C "$repo" add -A
assert "reference from a property" 'src/App/A.cs' 'all'
printf '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>\n<ProjectReference\n    Include="../Core/Core.csproj" />\n</ItemGroup></Project>\n' > "$repo/src/Odd/Odd.csproj"
assert "reference Include on its own line" 'src/App/A.cs' 'all'

[ "$fail" -eq 0 ] && echo "ok" || exit 1
