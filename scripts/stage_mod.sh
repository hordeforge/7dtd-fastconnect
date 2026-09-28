#!/usr/bin/env bash
# Copy the shipped mod payload from the build output into a staging tree.
#
# Usage: stage_mod.sh <build_dir> <stage_root>
#
# <build_dir> is what MSBuild writes (dist/7dtd-fastconnect); <stage_root>
# is the tree repro_zip.sh zips. The zip must contain the mod folder
# 7dtd-fastconnect/ at its top level, so files land in
# <stage_root>/7dtd-fastconnect/.
#
# The payload is an explicit list, not a directory copy: MSBuild leaves its
# whole output tree in place and never prunes what an earlier build wrote, so
# copying the directory ships the symbol file and any file a build since
# renamed or dropped. A missing payload file is fatal here, so a failed or
# partial build cannot be zipped under a release name.
#
# The staged mod folder is emptied first, so staging into a stage root a
# previous run already used converges on the payload instead of unioning the
# two: a file the payload list has since dropped, or anything a half-finished
# earlier run left behind, would otherwise ship in the zip and make the
# artifact depend on the stage root's history rather than on the build.
set -euo pipefail

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
	echo "Usage: stage_mod.sh <build_dir> <stage_root>"
	cat <<'EOF'

Copy the shipped mod payload (7dtd-fastconnect.dll, ModInfo.xml, LICENSE) from the
build output into <stage_root>/7dtd-fastconnect/. That folder is emptied
first, so staging into a stage root a previous run used produces the same
tree. Exits 1 if the build output lacks any of them, so a partial build is
never packaged.

Exit status: 0 files staged | 1 setup or payload failure | 2 usage error.

Key env vars: none
EOF
	exit 0
fi

if (( $# != 2 )); then
	echo "usage: ${0##*/} <build_dir> <stage_root> (got $# argument(s))" >&2
	exit 2
fi
BUILD_DIR="$1"
STAGE_ROOT="$2"

MOD_DIR_NAME=7dtd-fastconnect
# The mod payload: the assembly the game loads, the manifest it reads, and
# the license the zip redistributes under. Keep in sync with `make install`,
# which installs the same files.
PAYLOAD=(7dtd-fastconnect.dll ModInfo.xml LICENSE)

if [[ ! -d "$BUILD_DIR" ]]; then
	echo "ERROR: build output '$BUILD_DIR' does not exist; run make build first" >&2
	exit 1
fi
if [[ ! -d "$STAGE_ROOT" ]]; then
	echo "ERROR: stage root '$STAGE_ROOT' does not exist" >&2
	exit 1
fi

for f in "${PAYLOAD[@]}"; do
	if [[ ! -f "$BUILD_DIR/$f" ]]; then
		echo "ERROR: '$BUILD_DIR/$f' missing; build did not produce the mod payload" >&2
		exit 1
	fi
done

STAGE="$STAGE_ROOT/$MOD_DIR_NAME"
# Filled under a temp name and moved into place, so a copy that fails part-way
# (disk full, signal) never leaves a half-populated mod folder at the real
# path for a later repro_zip.sh to archive as if it were complete.
STAGE_TMP="$STAGE_ROOT/.$MOD_DIR_NAME.tmp.$$"
rm -rf "$STAGE_TMP"
trap 'rm -rf "$STAGE_TMP"' EXIT
mkdir -p "$STAGE_TMP"
for f in "${PAYLOAD[@]}"; do
	# -p keeps the mode; the zip normalizes timestamps via SOURCE_DATE_EPOCH.
	cp -p "$BUILD_DIR/$f" "$STAGE_TMP/$f"
done
# Empty the mod folder, not the whole stage root: that folder is this script's
# to own, and a sibling under the stage root belongs to the caller.
rm -rf "$STAGE"
mv -T "$STAGE_TMP" "$STAGE"
