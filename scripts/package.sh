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

make -C "$ROOT" build

VERSION="${VERSION:-$(git -C "$ROOT" describe --tags --always 2>/dev/null || true)}"
VERSION="${VERSION#v}"
if [[ -z "$VERSION" || "$VERSION" == *-* ]]; then
  # No tag yet (or dirty/untagged describe): fall back to a short commit id.
  VERSION="$(git -C "$ROOT" rev-parse --short HEAD)"
fi

OUT="$ROOT/dist/7dtd-fastconnect-$VERSION.zip"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
cp -a "$ROOT/dist/7dtd-fastconnect" "$STAGE/"
( cd "$STAGE" && zip -qr "$OUT" 7dtd-fastconnect )
echo "Packaged -> $OUT"
