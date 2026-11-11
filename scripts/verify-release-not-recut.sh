#!/usr/bin/env bash
# Compare this release commit with npm's record; the caller owns registry queries.
# Exit 0 + ok: no published version, so publishing may proceed.
# Exit 1 + republish: the same commit is published; npm versions are immutable.
# Exit 1 + moved: another commit is published; refuse a moved or re-cut tag.
# Exit 1 + error: the lookup is inconclusive; never treat a query error as absence.
set -euo pipefail
this_sha="${1:?usage: verify-release-not-recut.sh <this-sha> <published-sha-or-empty> [query-status: ok|error]}"
published_sha="${2:-}"
query_status="${3:-ok}"

if [ "$query_status" != "ok" ]; then
  echo "error"
  exit 1
fi

if [ -z "$published_sha" ]; then
  echo "ok"
  exit 0
fi

if [ "$published_sha" = "$this_sha" ]; then
  echo "republish"
  exit 1
fi

echo "moved"
exit 1
