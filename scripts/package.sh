#!/usr/bin/env bash
# Build the mod and package dist/7dtd-fastconnect into a distributable zip.
#
# The zip contains the 7dtd-fastconnect/ mod folder at its top level, so
# unzipping it inside <game>/Mods installs the mod (Mods/7dtd-fastconnect/).
#
# Version: taken from the newest git tag (vX.Y.Z -> X.Y.Z), or overridden
# with VERSION=x.y.z. Requires a local client install: the build compiles
# against the shipped Assembly-CSharp.dll, which this repo does not
# redistribute (see AGENTS.md).
#
# Reproducibility: entry mtimes come from SOURCE_DATE_EPOCH (default: the
# last commit's timestamp), never the wall clock, and scripts/repro_zip.sh
# normalizes order/metadata so two builds of one tree produce identical
# bytes. See scripts/repro_zip.sh for the full contract.
set -euo pipefail

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
	echo "Usage: package.sh"
	cat <<'EOF'

Build the mod (make build) and zip dist/7dtd-fastconnect into
dist/7dtd-fastconnect-<version>.zip with reproducible bytes: entry mtimes
come from SOURCE_DATE_EPOCH (default: the last commit's timestamp), never
the wall clock.

The version comes from the newest git tag (vX.Y.Z -> X.Y.Z); VERSION=x.y.z
overrides it, and a worktree with uncommitted tracked changes ships as
<commit>-dirty instead of claiming a release. Requires a local client
install (the build compiles against the shipped Assembly-CSharp.dll) and
zip on PATH.

Exit status: 0 zip written | 1 setup or build failure.

Key env vars:
  VERSION            override the version in the zip filename
  SOURCE_DATE_EPOCH  archive timestamp (default: last commit time)
EOF
	exit 0
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Feature-test like every other external tool in this repo: fail fast, before
# the multi-minute build, instead of a bare "zip: command not found" at the
# final zip step.
if ! command -v zip >/dev/null 2>&1; then
	echo "ERROR: zip not found on PATH; install zip to package." >&2
	exit 1
fi

make -C "$ROOT" build

VERSION="${VERSION:-$(git -C "$ROOT" describe --tags --always 2>/dev/null || true)}"
VERSION="${VERSION#v}"
if [[ -z "$VERSION" || "$VERSION" == *-* ]]; then
  # No tag yet (or dirty/untagged describe): fall back to a short commit id.
  VERSION="$(git -C "$ROOT" rev-parse --short HEAD)"
fi

# A worktree with uncommitted tracked changes must not ship under the tag's
# name: the artifact would claim to be a release while differing from it.
if ! git -C "$ROOT" diff-index --quiet HEAD -- 2>/dev/null; then
  VERSION="$(git -C "$ROOT" rev-parse --short HEAD)-dirty"
fi

# Archive timestamps default to the commit that produced this tree so the
# same checkout always zips identically; SOURCE_DATE_EPOCH overrides.
export SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "$ROOT" log -1 --pretty=%ct)}"

OUT="$ROOT/dist/7dtd-fastconnect-$VERSION.zip"
# Use a project-local staging dir instead of /tmp (tmpfs/RAM) so an
# interrupted package (SIGKILL) does not leak stage trees in volatile storage.
STAGE="$ROOT/dist/.package-stage-$$"
mkdir -p "$STAGE"
trap 'rm -rf "$STAGE"' EXIT INT TERM
cp -a "$ROOT/dist/7dtd-fastconnect" "$STAGE/"
"$ROOT/scripts/repro_zip.sh" "$STAGE" "$OUT"
echo "Packaged -> $OUT (epoch $SOURCE_DATE_EPOCH)"
