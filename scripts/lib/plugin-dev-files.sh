#!/usr/bin/env bash
# What the kcap/ plugin directory holds only for working on it, relative to that directory: the steering mod's
# tests, its type-check config and its hand-written typings. Nothing loads them, so no payload carries them.

is_plugin_dev_file() {
  case "$1" in
    *.test.ts | steering/tsconfig.json | steering/typings/*) return 0 ;;
  esac
  return 1
}

# Removes them from a copy of the plugin directory.
strip_plugin_dev_files() {
  find "$1" -type f -name '*.test.ts' -exec rm -f {} +
  rm -rf "$1/steering/typings" "$1/steering/tsconfig.json"
}
