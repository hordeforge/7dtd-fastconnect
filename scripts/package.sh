#!/usr/bin/env bash
# Build the mod and package dist/7dtd-fastconnect into a distributable zip.
#
# The zip contains the 7dtd-fastconnect/ mod folder at its top level, so
# unzipping it inside <game>/Mods installs the mod (Mods/7dtd-fastconnect/).
# It holds the mod payload only (scripts/stage_mod.sh), never the build's
# symbol file or leftovers from an earlier build.
#
# Version: taken from the newest git tag (vX.Y.Z -> X.Y.Z), or overridden
# with VERSION=x.y.z. Requires a local client install: the build compiles
# against the shipped Assembly-CSharp.dll, which this repo does not
# redistribute (see AGENTS.md).
#
# Reproducibility: entry mtimes come from SOURCE_DATE_EPOCH (default: the
# last commit's timestamp), never the wall clock, and scripts/repro_zip.sh
# normalizes order/metadata so two builds of one tree produce identical
# bytes. See scripts/repro_zip.sh for the full contract. A sibling .buildinfo
# beside the zip records the commit, SDK, epoch, and sha256 needed to rebuild
# it.
set -euo pipefail

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
	echo "Usage: package.sh"
	cat <<'EOF'

Build the mod (make build) and zip dist/7dtd-fastconnect into
dist/7dtd-fastconnect-<version>.zip with reproducible bytes: entry mtimes
come from SOURCE_DATE_EPOCH (default: the last commit's timestamp), never
the wall clock. A sibling .buildinfo records the commit, the dotnet SDK, and
the archive's sha256 so the zip can be reproduced later.

The version comes from the newest git tag (vX.Y.Z -> X.Y.Z); VERSION=x.y.z
overrides it, and a worktree with uncommitted tracked changes ships as
<commit>-dirty instead of claiming a release. Requires a local client
install (the build compiles against the shipped Assembly-CSharp.dll) and
zip on PATH.

Exit status: 0 zip written | 1 setup or build failure | 2 usage error.

Key env vars:
  VERSION            override the version in the zip filename
  SOURCE_DATE_EPOCH  archive timestamp (default: last commit time)
EOF
	exit 0
fi

# Takes no arguments, so a mistyped flag (--verison) would otherwise start a
# multi-minute build nobody asked for. Reject it the way the other entry
# points reject a bad argument, before anything touches disk.
if (( $# != 0 )); then
	echo "usage: ${0##*/} (takes no arguments; got $#)" >&2
	exit 2
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
"$ROOT/scripts/stage_mod.sh" "$ROOT/dist/7dtd-fastconnect" "$STAGE"
"$ROOT/scripts/repro_zip.sh" "$STAGE" "$OUT"

# What a rebuild needs to reproduce the zip: the exact inputs, the toolchain
# that compiled them, and the bytes that came out. Written beside the archive,
# never into it, so the payload stays the two files the game loads.
BUILDINFO="${OUT%.zip}.buildinfo"
{
	echo "version: $VERSION"
	echo "commit: $(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
	echo "dirty: $(git -C "$ROOT" diff-index --quiet HEAD -- 2>/dev/null && echo no || echo yes)"
	echo "source_date_epoch: $SOURCE_DATE_EPOCH"
	echo "dotnet: $(make -C "$ROOT" --no-print-directory dotnet-version 2>/dev/null || echo unknown)"
	echo "sha256: $(sha256sum "$OUT" | cut -d' ' -f1)"
} >"$BUILDINFO"

echo "Packaged -> $OUT (epoch $SOURCE_DATE_EPOCH)"
echo "Build record -> $BUILDINFO"
