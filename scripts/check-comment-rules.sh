#!/usr/bin/env bash
# Exit: 0 clean, 1 violation, 2 checker failure.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)" || exit 2
command -v python3 >/dev/null || { echo 'comment rules: Python 3.10+ is required' >&2; exit 2; }
exec python3 "$here/check-comment-rules.py"
