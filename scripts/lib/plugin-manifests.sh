#!/usr/bin/env bash
# The manifests a release stamps with its version, relative to the kcap/ plugin directory: Claude Code
# reads a plugin's payload as new only when its version moves.
# shellcheck disable=SC2034
plugin_manifests=(.claude-plugin/plugin.json steering/.claude-plugin/plugin.json)
