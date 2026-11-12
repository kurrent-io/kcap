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

csproj src/Odd/Odd.csproj '$(RepoRoot)/src/Core/Core.csproj'
git -C "$repo" add -A
assert "unresolvable reference"   'src/App/A.cs'                            'all'

[ "$fail" -eq 0 ] && echo "ok" || exit 1
