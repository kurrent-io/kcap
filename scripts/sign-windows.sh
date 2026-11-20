#!/usr/bin/env bash
# Authenticode-signs PE files through Azure Artifact Signing with jsign, which runs on Linux as well
# as Windows. jsign reads the access token from KCAP_SIGN_TOKEN itself, so the token never appears on
# a command line, where `ps` and vpk's log would show it.
# Usage: sign-windows.sh <file>...       signs the files and refuses any left without a signature
#        sign-windows.sh --vpk-template  prints the same signer as a vpk pack --signTemplate
# Needs JSIGN_JAR, ARTIFACT_SIGNING_ENDPOINT, ARTIFACT_SIGNING_ACCOUNT, ARTIFACT_SIGNING_PROFILE and,
# to sign, KCAP_SIGN_TOKEN.
set -euo pipefail
usage="usage: sign-windows.sh <file>... | --vpk-template"
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/pe.sh
source "$here/lib/pe.sh"

[ $# -gt 0 ] || { echo "$usage" >&2; exit 2; }
: "${JSIGN_JAR:?JSIGN_JAR is not set}" "${ARTIFACT_SIGNING_ENDPOINT:?ARTIFACT_SIGNING_ENDPOINT is not set}"
: "${ARTIFACT_SIGNING_ACCOUNT:?ARTIFACT_SIGNING_ACCOUNT is not set}" "${ARTIFACT_SIGNING_PROFILE:?ARTIFACT_SIGNING_PROFILE is not set}"
[ -f "$JSIGN_JAR" ] || { echo "sign-windows: $JSIGN_JAR does not exist" >&2; exit 1; }

endpoint="${ARTIFACT_SIGNING_ENDPOINT#https://}"
endpoint="${endpoint%/}"
# jsign uses any other scheme as given, and would send the token over it.
case "$endpoint" in
  ""|*/*|*:*) echo "sign-windows: ARTIFACT_SIGNING_ENDPOINT must be a host name or an https:// URL, not '$ARTIFACT_SIGNING_ENDPOINT'" >&2; exit 1 ;;
esac
opts=(--storetype TRUSTEDSIGNING --keystore "$endpoint" --storepass env:KCAP_SIGN_TOKEN
      --alias "$ARTIFACT_SIGNING_ACCOUNT/$ARTIFACT_SIGNING_PROFILE")

if [ "$1" = --vpk-template ]; then
  # vpk runs the template through cmd.exe, replacing {{file...}} with quoted Windows paths.
  jar="$JSIGN_JAR"
  if command -v cygpath >/dev/null 2>&1; then jar="$(cygpath -w "$jar")"; fi
  printf 'java -jar "%s" %s {{file...}}\n' "$jar" "${opts[*]}"
  exit 0
fi

: "${KCAP_SIGN_TOKEN:?KCAP_SIGN_TOKEN is not set}"
java -jar "$JSIGN_JAR" "${opts[@]}" "$@"
for f in "$@"; do
  pe_signed "$f" || { echo "sign-windows: $f carries no signature after signing" >&2; exit 1; }
done
echo "signed $# file(s) as $ARTIFACT_SIGNING_ACCOUNT/$ARTIFACT_SIGNING_PROFILE" >&2
