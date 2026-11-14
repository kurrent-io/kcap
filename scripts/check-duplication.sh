#!/usr/bin/env bash
# Exit: 0 at the baseline, 1 above or below it, 2 checker failure.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)" || exit 2
command -v python3 >/dev/null || { echo 'duplication: Python 3.10+ is required' >&2; exit 2; }
exec python3 "$here/check-duplication.py" "$@"
