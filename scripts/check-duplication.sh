#!/usr/bin/env bash
# Exit: 0 when every family is in the baseline, 1 on a family it does not list, 2 checker failure.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)" || exit 2
python="$(bash "$here/tools-python.sh")" || exit 2
exec "$python" "$here/check-duplication.py" "$@"
