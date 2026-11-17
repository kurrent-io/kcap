#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/sign-windows-bundle.sh"
# shellcheck source=lib/pe.sh
source "$here/lib/pe.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

# Writes the bytes of <hex> at <offset> in <file>.
put() { printf '%b' "$(sed 's/../\\x&/g' <<<"$3")" | dd of="$1" bs=1 seek="$2" conv=notrunc 2>/dev/null; }

# A PE32+ image with 16 data directories whose certificate table is <size> bytes, little-endian hex.
make_pe() {
  head -c 512 /dev/zero > "$1"
  put "$1" 0 4d5a; put "$1" 60 40000000; put "$1" 64 50450000
  put "$1" 88 0b02; put "$1" 196 10000000; put "$1" 236 "$2"
}

# Stands in for jsign: gives every file argument a certificate table and logs each call's arguments.
# FAKE_JSIGN=noop leaves the files as they were; FAKE_JSIGN=fail exits 1.
mkdir -p "$tmp/bin"
{
  echo '#!/usr/bin/env bash'
  declare -f put
  cat <<'EOF'
printf '%s\n' "$*" >> "$FAKE_LOG"
[ "${FAKE_JSIGN:-}" = fail ] && exit 1
skip=
for a in "$@"; do
  if [ -n "$skip" ]; then skip=; continue; fi
  if [ "$a" = -jar ]; then skip=1; continue; fi
  if [ -f "$a" ] && [ "${FAKE_JSIGN:-}" != noop ]; then put "$a" 236 00080000; fi
done
EOF
} > "$tmp/bin/java"
chmod +x "$tmp/bin/java"
: > "$tmp/jsign.jar"

signer=(PATH="$tmp/bin:$PATH" FAKE_LOG="$tmp/java.log" JSIGN_JAR="$tmp/jsign.jar"
        ARTIFACT_SIGNING_ENDPOINT=https://eus.codesigning.azure.net/ ARTIFACT_SIGNING_ACCOUNT=acct
        ARTIFACT_SIGNING_PROFILE=prof KCAP_SIGN_TOKEN=s3cret-token)

# Lays out an unsigned server and Postgres, a signed runtime DLL and a text file named .exe.
layout() {
  mkdir -p "$1/postgres/bin"
  make_pe "$1/kcap-server.exe" 00000000
  make_pe "$1/postgres/bin/postgres.exe" 00000000
  make_pe "$1/vcruntime140.dll" 00080000
  printf 'text\n' > "$1/notes.exe"
}

fail=0
check() { # <name> <condition...>
  local name="$1"; shift
  if ! "$@"; then echo "FAIL: $name"; fail=1; fi
}
# Runs the bundle signer on <dir> with the signer environment plus [VAR=value...]; prints its exit code.
run() {
  local dir="$1" rc=0; shift
  : > "$tmp/java.log"
  env "${signer[@]}" "$@" bash "$sh" "$dir" >/dev/null 2>&1 || rc=$?
  echo "$rc"
}
logged() { grep -qF -- "$1" "$tmp/java.log"; }
not_logged() { ! grep -qF -- "$1" "$tmp/java.log"; }

layout "$tmp/a"
check signs-unsigned          [ "$(run "$tmp/a")" = 0 ]
check server-signed           pe_signed "$tmp/a/kcap-server.exe"
check postgres-signed         pe_signed "$tmp/a/postgres/bin/postgres.exe"
check one-jsign-call          [ "$(wc -l < "$tmp/java.log" | tr -d ' ')" = 1 ]
check signed-dll-left-alone   not_logged vcruntime140.dll
check text-file-left-alone    not_logged notes.exe
check endpoint-normalised     logged '--keystore eus.codesigning.azure.net --'
check token-read-from-env     logged '--storepass env:KCAP_SIGN_TOKEN'
check alias                   logged '--alias acct/prof'
check token-not-on-cmdline    not_logged s3cret-token

layout "$tmp/b"
check signer-leaves-unsigned  [ "$(run "$tmp/b" FAKE_JSIGN=noop)" != 0 ]
layout "$tmp/c"
check signer-fails            [ "$(run "$tmp/c" FAKE_JSIGN=fail)" != 0 ]
layout "$tmp/d"
check no-token                [ "$(run "$tmp/d" KCAP_SIGN_TOKEN=)" != 0 ]
check no-token-no-call        [ ! -s "$tmp/java.log" ]

for endpoint in http://eus.codesigning.azure.net ftp://eus.codesigning.azure.net eus.codesigning.azure.net:80; do
  layout "$tmp/endpoint"
  check "endpoint-refused:$endpoint" [ "$(run "$tmp/endpoint" ARTIFACT_SIGNING_ENDPOINT="$endpoint")" != 0 ]
  check "endpoint-refused-no-call:$endpoint" [ ! -s "$tmp/java.log" ]
  rm -rf "$tmp/endpoint"
done

mkdir -p "$tmp/e"; make_pe "$tmp/e/kcap-server.exe" 00080000
check nothing-to-sign         [ "$(run "$tmp/e")" = 0 ]
check nothing-to-sign-no-call [ ! -s "$tmp/java.log" ]

mkdir -p "$tmp/f"; make_pe "$tmp/f/other.exe" 00000000
check no-server               [ "$(run "$tmp/f")" != 0 ]

template="$(env "${signer[@]}" KCAP_SIGN_TOKEN= bash "$here/sign-windows.sh" --vpk-template)"
want="java -jar \"$tmp/jsign.jar\" --storetype TRUSTEDSIGNING --keystore eus.codesigning.azure.net --storepass env:KCAP_SIGN_TOKEN --alias acct/prof {{file...}}"
check vpk-template            [ "$template" = "$want" ]

[ "$fail" -eq 0 ] && echo "ok" || exit 1
