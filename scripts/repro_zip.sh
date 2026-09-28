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
#   - Modes are normalized (dirs 0755, files 0644), because zip records the
#     permission bits and they otherwise follow the caller's umask or the
#     staged file's own mode, so two hosts packaging one tree disagreed.
set -euo pipefail

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
	echo "Usage: repro_zip.sh <stage_dir> <out.zip>"
	cat <<'EOF'

Create a byte-reproducible zip from a staged mod directory: every entry's
mtime is rewritten to SOURCE_DATE_EPOCH, entry order comes from an explicit
C-locale sort, modes are normalized, and uid/gid plus platform-specific extra
fields are stripped (zip -X). Two runs over one tree produce identical bytes.

Exit status: 0 zip written | 1 setup failure | 2 usage error.

Key env vars:
  SOURCE_DATE_EPOCH  required; seconds since 1970 UTC stamped on every
                     entry (package.sh defaults it to the last commit)
EOF
	exit 0
fi

if (( $# != 2 )); then
	echo "usage: ${0##*/} <stage_dir> <out.zip> (got $# argument(s))" >&2
	exit 2
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

# Normalize modes for the same reason. The archive records the permission
# bits, and they come from the caller's umask (mkdir) or the build's own file
# (cp -p in stage_mod.sh), not from the source tree.
find "$STAGE" -type d -exec chmod 0755 {} +
find "$STAGE" -type f -exec chmod 0644 {} +

OUT="$(cd "$(dirname "$OUT")" && pwd)/$(basename "$OUT")"
# The archive at the release path is left alone until the new one is complete.
# Nothing is zipped into it directly, so a zip left by an earlier run cannot
# leak deleted entries into the new archive the way an update-in-place would;
# the rename below replaces it atomically. Deleting it up front instead would
# mean a run that fails or is killed after this point (no zip, full disk)
# destroys a good archive an earlier run produced and leaves nothing behind.
# zipped into a temp file in the destination directory and renamed, so a zip
# that fails or is killed part-way (disk full, signal) never leaves a
# truncated archive at the release path looking like a finished artifact.
OUT_TMP="$OUT.tmp.$$"
rm -f "$OUT_TMP"
if ! (
	cd "$STAGE"
	find . -print | sort | zip -q -X "$OUT_TMP" -@
); then
	rm -f "$OUT_TMP"
	echo "ERROR: zip failed for $STAGE; no archive written" >&2
	exit 1
fi
mv -f "$OUT_TMP" "$OUT"
