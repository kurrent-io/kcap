#!/usr/bin/env bash
# Writes the entitlements the app bundle's main executable is signed with. Given a Developer ID provisioning
# profile, it embeds the profile in the bundle and adds the time-sensitive notification entitlement the profile
# grants, so permission requests and questions come through a Focus. Without one the base entitlements are used
# unchanged: a restricted entitlement no embedded profile backs stops the app launching at all.
# Usage: app-bundle-entitlements.sh <base.entitlements> <bundle.app> <out.entitlements> [profile.provisionprofile]
set -euo pipefail
usage="usage: app-bundle-entitlements.sh <base.entitlements> <bundle.app> <out.entitlements> [profile.provisionprofile]"
base="${1:?$usage}"; bundle="${2:?$usage}"; out="${3:?$usage}"; profile="${4:-}"
buddy=/usr/libexec/PlistBuddy
time_sensitive=com.apple.developer.usernotifications.time-sensitive

[ -f "$base" ] || { echo "entitlements file not found: $base" >&2; exit 1; }
[ -f "$bundle/Contents/Info.plist" ] || { echo "not an app bundle: $bundle" >&2; exit 1; }
cp "$base" "$out"
if [ -z "$profile" ]; then
  echo "no provisioning profile: the app is signed without the time-sensitive notification entitlement" >&2
  exit 0
fi

decoded="$(mktemp)"; trap 'rm -f "$decoded"' EXIT
security cms -D -i "$profile" > "$decoded"
bundle_id="$("$buddy" -c 'Print :CFBundleIdentifier' "$bundle/Contents/Info.plist")"
team="$("$buddy" -c 'Print :TeamIdentifier:0' "$decoded")"
app_id="$("$buddy" -c 'Print :Entitlements:com.apple.application-identifier' "$decoded")"
[ "$app_id" = "$team.$bundle_id" ] || { echo "the profile is for $app_id, not $team.$bundle_id" >&2; exit 1; }
[ "$("$buddy" -c "Print :Entitlements:$time_sensitive" "$decoded" 2>/dev/null)" = true ] \
  || { echo "the profile does not grant $time_sensitive" >&2; exit 1; }
expires="$(plutil -extract ExpirationDate raw -o - "$decoded")"
[[ "$expires" > "$(date -u +%Y-%m-%dT%H:%M:%SZ)" ]] || { echo "the profile expired at $expires" >&2; exit 1; }

cp "$profile" "$bundle/Contents/embedded.provisionprofile"
"$buddy" -c "Add :com.apple.application-identifier string $app_id" "$out"
"$buddy" -c "Add :com.apple.developer.team-identifier string $team" "$out"
"$buddy" -c "Add :$time_sensitive bool true" "$out"
plutil -lint "$out" >/dev/null
echo "embedded the provisioning profile for $app_id (expires $expires)" >&2
