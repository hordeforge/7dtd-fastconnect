#!/usr/bin/env bash
# Build the mod and package dist/7dtd-fastconnect into a distributable zip.
#
# The zip contains the 7dtd-fastconnect/ mod folder at its top level, so
# unzipping it inside <game>/Mods installs the mod (Mods/7dtd-fastconnect/).
# It holds the mod payload only (scripts/stage_mod.sh), never the build's
# symbol file or leftovers from an earlier build.
#
# Version: the tag HEAD sits on (vX.Y.Z -> X.Y.Z), or overridden with
# VERSION=x.y.z. A commit past the newest tag, or a dirty tree, has no usable
# version and falls back to a short commit id. Requires a local client install: the build compiles
# against the shipped Assembly-CSharp.dll, which this repo does not
# redistribute (see AGENTS.md).
#
# Reproducibility: entry mtimes come from SOURCE_DATE_EPOCH (default: the
# last commit's timestamp), never the wall clock, and scripts/repro_zip.sh
# normalizes order/metadata so two builds of one tree produce identical
# bytes. See scripts/repro_zip.sh for the full contract. A sibling .buildinfo
# beside the zip records the commit, SDK, epoch, and sha256 needed to rebuild
# it. Before the run reports success it reads the archive back and requires it
# to hold exactly the staged tree, so a wrong or truncated zip fails here
# rather than at the point someone uploads it.
set -euo pipefail

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
	echo "Usage: package.sh"
	cat <<'EOF'

Build the mod (make build) and zip dist/7dtd-fastconnect into
dist/7dtd-fastconnect-<version>.zip with reproducible bytes: entry mtimes
come from SOURCE_DATE_EPOCH (default: the last commit's timestamp), never
the wall clock. A sibling .buildinfo records the commit, the dotnet SDK, and
the archive's sha256 so the zip can be reproduced later.

The version is the tag HEAD sits on (vX.Y.Z -> X.Y.Z); VERSION=x.y.z
overrides it. Any commit past that tag ships as its short commit id rather
than claiming a release. The written archive is read back and compared with
the staged payload before success is reported. Requires a local client
install (the build compiles against the shipped Assembly-CSharp.dll) and
zip/unzip on PATH.

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
# final zip step. unzip is the verifier below, so it is tested here too rather
# than after the build has already spent its minutes. sha256sum is on the same
# path: the build record carries a sha256 of the archive, and this script fails
# rather than write an empty one, so a host without the tool cannot package at
# all.
for tool in zip unzip sha256sum; do
	if ! command -v "$tool" >/dev/null 2>&1; then
		echo "ERROR: $tool not found on PATH; install zip/unzip/coreutils to package." >&2
		exit 1
	fi
done

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

# VERSION names the archive, its .buildinfo, and both sit under dist/. A tag
# may carry a slash ("release/1.0" describes as "release/1.0-0-gabc123"), and
# VERSION= is a documented override, so either can aim the zip outside dist/ or
# at an existing file. Keep it to one filename-safe component, the same rule
# one_shot_join.sh applies to CYCLE; a rejected value is a usage error rather
# than a silent fallback, since the fallback would ship a wrong version name.
if ! [[ "$VERSION" =~ ^[0-9A-Za-z][0-9A-Za-z._-]*$ ]]; then
  echo "ERROR: version '$VERSION' is not a filename-safe version (allowed: digits, letters, '.', '_', '-', starting with a digit or letter)" >&2
  exit 2
fi

# Archive timestamps default to the commit that produced this tree so the
# same checkout always zips identically; SOURCE_DATE_EPOCH overrides.
export SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "$ROOT" log -1 --pretty=%ct)}"

OUT="$ROOT/dist/7dtd-fastconnect-$VERSION.zip"
# Use a project-local staging dir instead of /tmp (tmpfs/RAM) so an
# interrupted package (SIGKILL) does not leak stage trees in volatile storage.
STAGE="$ROOT/dist/.package-stage-$$"
# The archive is built and read back at this path, then renamed onto the
# release path, so the path a release is published at only ever holds an
# archive this run verified. A rerun that fails the verification below leaves
# the previous run's zip in place instead of replacing a good release with a
# broken one.
CANDIDATE="$ROOT/dist/.package-candidate-$$"
# A killed run cannot run its trap, so its per-run stage and candidate paths
# (both named by pid) stay behind forever. Drop anything older than a day:
# long enough that no run this one is racing is still using it.
find "$ROOT/dist" -maxdepth 1 -name '.package-stage-*' -mmin +1440 -exec rm -rf {} + 2>/dev/null || true
find "$ROOT/dist" -maxdepth 1 -name '.package-candidate-*' -mmin +1440 -delete 2>/dev/null || true
mkdir -p "$STAGE"
# Signals must exit, not fall through: a non-exiting INT/TERM handler removes
# the stage and the script continues into repro_zip.sh, which then reports
# "stage dir does not exist" as a setup failure and exits 1 instead of 130.
trap 'rm -rf "$STAGE"; rm -f "$CANDIDATE"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
"$ROOT/scripts/stage_mod.sh" "$ROOT/dist/7dtd-fastconnect" "$STAGE"
"$ROOT/scripts/repro_zip.sh" "$STAGE" "$CANDIDATE"

# Read the archive back and require it to hold exactly the staged tree. A zip
# that lost an entry, gained one, or is unreadable is not a release, and the
# only check on it until now was a human running `unzip -l` from
# docs/RELEASING.md after the run reported success. Both listings have their
# directory entries' trailing slash stripped first, since zip records one and
# find does not. Comparing the sorted lists (LC_ALL=C on both sides) pins
# both directions: a missing payload file and a leaked build leftover both
# fail.
archived="$(unzip -Z1 "$CANDIDATE" | sed 's|/$||' | LC_ALL=C sort)"
staged="$(cd "$STAGE" && find . -mindepth 1 | sed 's|^\./||;s|/$||' | LC_ALL=C sort)"
if [[ "$archived" != "$staged" ]]; then
	echo "ERROR: archive contents do not match the staged payload:" >&2
	diff <(printf '%s\n' "$staged") <(printf '%s\n' "$archived") >&2 || true
	echo "ERROR: nothing published to $OUT; any zip already there is an earlier run's" >&2
	exit 1
fi
# Verified, so the release path gets it.
mv -f "$CANDIDATE" "$OUT"

# What a rebuild needs to reproduce the zip: the exact inputs, the toolchain
# that compiled them, and the bytes that came out. Written beside the archive,
# never into it, so the payload stays the files the game loads.
BUILDINFO="${OUT%.zip}.buildinfo"
# shasum is the BSD/macOS spelling of the same digest; coreutils' sha256sum
# does not exist there, and a missing one would have written an empty sha256
# into the build record that claims to make the zip reproducible. Neither tool
# present is a setup failure, not a record with a blank field. The digest is
# taken out of band, before the record is written, so a failing hash is
# visible instead of leaving an empty field behind.
if command -v sha256sum >/dev/null 2>&1; then
	SHA256="$(sha256sum "$OUT" | cut -d' ' -f1)"
elif command -v shasum >/dev/null 2>&1; then
	SHA256="$(shasum -a 256 "$OUT" | cut -d' ' -f1)"
else
	echo "ERROR: neither sha256sum nor shasum found on PATH; cannot record the archive digest" >&2
	exit 1
fi
if [[ -z "$SHA256" ]]; then
	echo "ERROR: no sha256 digest produced for $OUT" >&2
	exit 1
fi
# Written through a temp file and renamed, so an interrupted write cannot
# leave a half record beside a good zip.
BUILDINFO_TMP="$BUILDINFO.tmp.$$"
{
	echo "version: $VERSION"
	echo "commit: $(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
	echo "dirty: $(git -C "$ROOT" diff-index --quiet HEAD -- 2>/dev/null && echo no || echo yes)"
	echo "source_date_epoch: $SOURCE_DATE_EPOCH"
	echo "dotnet: $(make -C "$ROOT" --no-print-directory dotnet-version 2>/dev/null || echo unknown)"
	echo "sha256: $SHA256"
} >"$BUILDINFO_TMP"
mv -f "$BUILDINFO_TMP" "$BUILDINFO"

echo "Packaged -> $OUT (epoch $SOURCE_DATE_EPOCH)"
echo "Build record -> $BUILDINFO"
