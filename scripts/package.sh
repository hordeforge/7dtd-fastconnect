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
set -euo pipefail
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

OUT="$ROOT/dist/7dtd-fastconnect-$VERSION.zip"
# Use a project-local staging dir instead of /tmp (tmpfs/RAM) so an
# interrupted package (SIGKILL) does not leak stage trees in volatile storage.
STAGE="$ROOT/dist/.package-stage-$$"
mkdir -p "$STAGE"
trap 'rm -rf "$STAGE"' EXIT INT TERM
cp -a "$ROOT/dist/7dtd-fastconnect" "$STAGE/"
# Start from an empty archive: zip -r updates entries into an existing file,
# so a stale or truncated zip left by an interrupted run would keep deleted
# files in the shipped artifact or fail confusingly mid-update.
rm -f "$OUT"
( cd "$STAGE" && zip -qr "$OUT" 7dtd-fastconnect )
echo "Packaged -> $OUT"
