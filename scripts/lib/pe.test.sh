#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=pe.sh
source "$here/pe.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

# Writes the bytes of <hex> at <offset> in <file>.
put() { printf '%b' "$(sed 's/../\\x&/g' <<<"$3")" | dd of="$1" bs=1 seek="$2" conv=notrunc 2>/dev/null; }

# Writes a minimal PE image: <32|64> picks the optional header, <dirs> the data directory count as
# little-endian hex, <cert-size> the certificate table's size as little-endian hex.
make_pe() {
  local f="$1" opt=88 count dirs
  head -c 512 /dev/zero > "$f"
  put "$f" 0 4d5a
  put "$f" 60 40000000
  put "$f" 64 50450000
  if [ "$2" = 64 ]; then put "$f" "$opt" 0b02; count=$(( opt + 108 )); dirs=$(( opt + 112 ))
  else put "$f" "$opt" 0b01; count=$(( opt + 92 )); dirs=$(( opt + 96 )); fi
  put "$f" "$count" "$3"
  put "$f" $(( dirs + 36 )) "$4"
}

fail=0
expect() { # <name> <want: yes|no> <command...>
  local name="$1" want="$2" got=no; shift 2
  if "$@"; then got=yes; fi
  if [ "$got" != "$want" ]; then echo "FAIL: $name -> $got (want $want)"; fail=1; fi
}

make_pe "$tmp/pe64-unsigned.exe" 64 10000000 00000000
make_pe "$tmp/pe64-signed.exe"   64 10000000 00080000
make_pe "$tmp/pe32-unsigned.dll" 32 10000000 00000000
make_pe "$tmp/pe32-signed.dll"   32 10000000 00080000
# Too few data directories to hold a certificate table: the bytes where it would sit are not one.
make_pe "$tmp/no-cert-dir.dll"   64 04000000 00080000
head -c 512 /dev/zero > "$tmp/dos-only.exe"; put "$tmp/dos-only.exe" 0 4d5a; put "$tmp/dos-only.exe" 60 40000000
head -c 64 /dev/zero > "$tmp/lfanew-past-end.exe"; put "$tmp/lfanew-past-end.exe" 0 4d5a; put "$tmp/lfanew-past-end.exe" 60 00100000
printf 'MZ' > "$tmp/truncated.exe"
printf 'not a binary\n' > "$tmp/readme.exe"

expect pe64-unsigned-is-image   yes pe_image  "$tmp/pe64-unsigned.exe"
expect pe64-unsigned-not-signed no  pe_signed "$tmp/pe64-unsigned.exe"
expect pe64-signed              yes pe_signed "$tmp/pe64-signed.exe"
expect pe32-unsigned-is-image   yes pe_image  "$tmp/pe32-unsigned.dll"
expect pe32-unsigned-not-signed no  pe_signed "$tmp/pe32-unsigned.dll"
expect pe32-signed              yes pe_signed "$tmp/pe32-signed.dll"
expect no-cert-dir-not-signed   no  pe_signed "$tmp/no-cert-dir.dll"
expect dos-only-not-image       no  pe_image  "$tmp/dos-only.exe"
expect lfanew-past-end          no  pe_image  "$tmp/lfanew-past-end.exe"
expect truncated-not-image      no  pe_image  "$tmp/truncated.exe"
expect text-not-image           no  pe_image  "$tmp/readme.exe"
expect text-not-signed          no  pe_signed "$tmp/readme.exe"

mkdir -p "$tmp/tree/sub"
make_pe "$tmp/tree/sub/b.dll" 64 10000000 00000000
make_pe "$tmp/tree/a.exe" 32 10000000 00080000
make_pe "$tmp/tree/no-extension" 64 10000000 00000000
printf 'text\n' > "$tmp/tree/notes.txt"
got="$(pe_files "$tmp/tree" | sed "s|^$tmp/tree/||" | tr '\n' ' ')"
want="a.exe no-extension sub/b.dll "
if [ "$got" != "$want" ]; then echo "FAIL: pe_files -> '$got' (want '$want')"; fail=1; fi

[ "$fail" -eq 0 ] && echo "ok" || exit 1
