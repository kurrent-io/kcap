#!/usr/bin/env bash
set -euo pipefail
if [ "$(uname -s)" != Darwin ]; then echo "skipped (macOS signing)"; exit 0; fi
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/app-bundle-entitlements.sh"
base="$here/../src/Capacitor.App/Packaging/app.entitlements"
buddy=/usr/libexec/PlistBuddy
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

# Stands in for `security cms -D -i <profile>`: the test's profiles are already decoded plists.
mkdir -p "$tmp/bin"
cat > "$tmp/bin/security" <<'EOF'
#!/usr/bin/env bash
[ "$1 $2 $3" = "cms -D -i" ] || { echo "unexpected security call: $*" >&2; exit 2; }
cat "$4"
EOF
chmod +x "$tmp/bin/security"
export PATH="$tmp/bin:$PATH"

# Stand-ins for DER certificates: the profile names the signing one second, after another.
signing_cert="$(printf 'the signing certificate' | base64)"
signing_sha="$(printf 'the signing certificate' | shasum -a 1 | cut -c1-40)"
other_cert="$(printf 'another certificate' | base64)"

# <name> <application-identifier> <grants-time-sensitive> <expiration> <provisions-all-devices> <certificate>
make_profile() {
  local p="$tmp/$1.provisionprofile"
  cat > "$p" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>TeamIdentifier</key><array><string>TEAM123</string></array>
  <key>ExpirationDate</key><date>$4</date>
  <key>ProvisionsAllDevices</key><$5/>
  <key>DeveloperCertificates</key><array><data>$other_cert</data><data>$6</data></array>
  <key>Entitlements</key><dict>
    <key>com.apple.application-identifier</key><string>$2</string>
    <key>com.apple.developer.usernotifications.time-sensitive</key><$3/>
  </dict>
</dict></plist>
EOF
  echo "$p"
}

new_bundle() {
  rm -rf "$tmp/kcap.app"; mkdir -p "$tmp/kcap.app/Contents"
  bash "$here/render-info-plist.sh" 1.5.0 "$tmp/kcap.app/Contents/Info.plist"
}

fail() { echo "FAIL: $*"; exit 1; }

new_bundle
bash "$sh" "$base" "$tmp/kcap.app" "$tmp/out.entitlements" 2>/dev/null
cmp "$base" "$tmp/out.entitlements" || fail "no profile must leave the base entitlements unchanged"
[ ! -e "$tmp/kcap.app/Contents/embedded.provisionprofile" ] || fail "embedded a profile that was not given"

good="$(make_profile good TEAM123.io.kurrent.kcap true 2099-01-01T00:00:00Z true "$signing_cert")"
bash "$sh" "$base" "$tmp/kcap.app" "$tmp/out.entitlements" "$good" "$signing_sha" 2>/dev/null
cmp "$good" "$tmp/kcap.app/Contents/embedded.provisionprofile" || fail "the profile was not embedded"
[ "$("$buddy" -c 'Print :com.apple.application-identifier' "$tmp/out.entitlements")" = TEAM123.io.kurrent.kcap ] || fail "application identifier"
[ "$("$buddy" -c 'Print :com.apple.developer.team-identifier' "$tmp/out.entitlements")" = TEAM123 ] || fail "team identifier"
[ "$("$buddy" -c 'Print :com.apple.developer.usernotifications.time-sensitive' "$tmp/out.entitlements")" = true ] || fail "time-sensitive"
[ "$("$buddy" -c 'Print :com.apple.security.cs.allow-jit' "$tmp/out.entitlements")" = true ] || fail "the base entitlements were dropped"

for bad in "other TEAM123.io.example.other true 2099-01-01T00:00:00Z true $signing_cert" \
           "ungranted TEAM123.io.kurrent.kcap false 2099-01-01T00:00:00Z true $signing_cert" \
           "expired TEAM123.io.kurrent.kcap true 2001-01-01T00:00:00Z true $signing_cert" \
           "development TEAM123.io.kurrent.kcap true 2099-01-01T00:00:00Z false $signing_cert" \
           "unsigned TEAM123.io.kurrent.kcap true 2099-01-01T00:00:00Z true $other_cert"; do
  # shellcheck disable=SC2086
  profile="$(make_profile $bad)"
  new_bundle
  if bash "$sh" "$base" "$tmp/kcap.app" "$tmp/out.entitlements" "$profile" "$signing_sha" 2>/dev/null; then
    fail "accepted the ${bad%% *} profile"
  fi
  [ ! -e "$tmp/kcap.app/Contents/embedded.provisionprofile" ] || fail "embedded the ${bad%% *} profile"
done
echo "ok"
