#!/usr/bin/env bash
# PE images read by their headers, so a Linux runner can tell which files Authenticode applies to and
# which already carry a signature.

# Prints the unsigned little-endian integer of <width> bytes at <offset> in <file>, or nothing when
# the file ends first. od reads in host byte order, and every runner is little-endian.
_pe_uint() {
  od -An -v -tu"$3" -j "$2" -N "$3" "$1" 2>/dev/null | tr -d ' \n'
}

# Succeeds when <file> is a PE image: an MZ header whose e_lfanew points at the PE signature.
pe_image() {
  local lfanew
  [ "$(head -c 2 "$1" 2>/dev/null)" = MZ ] || return 1
  lfanew="$(_pe_uint "$1" 60 4)"
  [ -n "$lfanew" ] || return 1
  [ "$(od -An -v -tx1 -j "$lfanew" -N 4 "$1" 2>/dev/null | tr -d ' \n')" = 50450000 ]
}

# Succeeds when <file> is a PE image whose certificate table, data directory 4, is not empty.
pe_signed() {
  local lfanew opt magic count dirs size
  pe_image "$1" || return 1
  lfanew="$(_pe_uint "$1" 60 4)"
  opt=$(( lfanew + 24 ))
  magic="$(_pe_uint "$1" "$opt" 2)"
  case "$magic" in
    267) count="$(_pe_uint "$1" $(( opt + 92 )) 4)"; dirs=$(( opt + 96 )) ;;
    523) count="$(_pe_uint "$1" $(( opt + 108 )) 4)"; dirs=$(( opt + 112 )) ;;
    *) return 1 ;;
  esac
  [ -n "$count" ] && [ "$count" -gt 4 ] || return 1
  size="$(_pe_uint "$1" $(( dirs + 4 * 8 + 4 )) 4)"
  [ -n "$size" ] && [ "$size" -gt 0 ]
}

# Prints every PE image under <dir>, sorted. Found by content: a bundle's binaries do not all carry
# an .exe or .dll name.
pe_files() {
  find "$1" -type f | LC_ALL=C sort | while IFS= read -r f; do
    if pe_image "$f"; then printf '%s\n' "$f"; fi
  done
}
