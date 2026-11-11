#!/usr/bin/env bash
# Exit 0: clean; 1: violations; 2: checker failure. Requires Python 3.9+ and Git.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
command -v python3 >/dev/null || { echo "Python 3.9+ is required" >&2; exit 2; }
exec python3 "$here/check-comment-rules.py" --linear-ids-only "$@"
