#!/usr/bin/env bash
# Create a byte-reproducible zip from a staged mod directory.
#
# Usage: repro_zip.sh <stage_dir> <out.zip>
#
# Determinism contract (reproducible-builds.org practice):
#   - SOURCE_DATE_EPOCH (seconds since 1970 UTC) must be set; every entry's
#     mtime is rewritten to it first, so wall-clock build time never reaches
#     the artifact. The staging tree is normalized in place.
#   - TZ=UTC and LC_ALL=C are pinned so timezone/locale cannot leak into
#     timestamps or ordering.
#   - Entry order comes from an explicit C-locale sort, never readdir order.
#   - zip -X strips uid/gid and platform-specific extra fields.
set -euo pipefail

if (( $# != 2 )); then
	echo "usage: $0 <stage_dir> <out.zip>" >&2
	exit 1
fi
STAGE="$1"
OUT="$2"

if [[ -z "${SOURCE_DATE_EPOCH:-}" ]]; then
	echo "ERROR: SOURCE_DATE_EPOCH must be set (seconds since 1970 UTC)" >&2
	exit 1
fi
case "$SOURCE_DATE_EPOCH" in
'' | *[!0-9]*)
	echo "ERROR: SOURCE_DATE_EPOCH must be a non-negative integer" >&2
	exit 1
	;;
esac
if ! command -v zip >/dev/null 2>&1; then
	echo "ERROR: zip not found on PATH; install zip to package." >&2
	exit 1
fi
if [[ ! -d "$STAGE" ]]; then
	echo "ERROR: stage dir '$STAGE' does not exist" >&2
	exit 1
fi

export TZ=UTC LC_ALL=C
# GNU date wants -d @epoch, BSD date wants -r epoch; accept either host.
STAMP="$(date -u -d "@$SOURCE_DATE_EPOCH" '+%Y%m%d%H%M.%S' 2>/dev/null \
	|| date -u -r "$SOURCE_DATE_EPOCH" '+%Y%m%d%H%M.%S')"

# Normalize mtimes in place; -depth touches children before parents so parent
# directory times survive.
find "$STAGE" -depth -exec touch -t "$STAMP" {} +

OUT="$(cd "$(dirname "$OUT")" && pwd)/$(basename "$OUT")"
# Start from an empty archive: zip updates entries into an existing file, so a
# stale or truncated zip left by an interrupted run would keep deleted files
# in the shipped artifact or fail confusingly mid-update.
rm -f "$OUT"
(
	cd "$STAGE"
	find . -print | sort | zip -q -X "$OUT" -@
)
