#!/usr/bin/env bash
# Offline gate for the release payload of scripts/stage_mod.sh: the shipped
# zip must contain the mod folder holding exactly the two installed files, a
# build output that also holds a symbol file or a file an earlier build left
# behind must not leak them, and a build missing part of the payload must
# fail instead of producing a zip under a release name.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"
STAGE_MOD="$ROOT/scripts/stage_mod.sh"

WORK="$(scratch_mktemp "$ROOT" 7dtd-stagemod)"
trap 'rm -rf "$WORK"' EXIT
# repro_zip.sh refuses to run without it; the epoch is a fixture choice here,
# not part of what this gate pins.
export SOURCE_DATE_EPOCH=1700000000

build_ok() {
	local d="$WORK/$1"
	mkdir -p "$d"
	printf 'dll' >"$d/7dtd-fastconnect.dll"
	printf '<xml/>\n' >"$d/ModInfo.xml"
	# What a real build also leaves in dist/: the symbol file, and a dll
	# from a build before an assembly rename that MSBuild never prunes.
	printf 'pdb' >"$d/7dtd-fastconnect.pdb"
	printf 'old' >"$d/7dtd-fastconnect.old.dll"
	printf '%s' "$d"
}

zip_entries() {
	unzip -Z1 "$1" | LC_ALL=C sort
}

# 1. Payload lands under the mod folder at the zip's top level.
d="$(build_ok full)"
mkdir -p "$WORK/stage-full"
"$STAGE_MOD" "$d" "$WORK/stage-full"
"$ROOT/scripts/repro_zip.sh" "$WORK/stage-full" "$WORK/full.zip" >/dev/null
entries="$(zip_entries "$WORK/full.zip")"
assert "staged tree holds only the two payload files" \
	test "$entries" = "7dtd-fastconnect/
7dtd-fastconnect/7dtd-fastconnect.dll
7dtd-fastconnect/ModInfo.xml"

# 2. The zip's top level is the mod folder, so unzipping into <game>/Mods
#    installs it where the game loader looks.
assert "zip has the mod folder at its top level" \
	test "$(printf '%s\n' "$entries" | head -1)" = "7dtd-fastconnect/"

# 3. The installed file set and the packaged one are the same set: a user
#    who `make install`s and a user who unzips a release get the same mod.
#    make install copies both files in one cp -f of "$(DIST)/A" "$(DIST)/B".
installed_files() {
	sed -n 's/.*cp -f "\$(DIST)\/\([^"]*\)" "\$(DIST)\/\([^"]*\)".*/\1\n\2/p' \
		"$ROOT/Makefile" | LC_ALL=C sort
}
packaged_files() {
	sed -n 's/^PAYLOAD=(\(.*\))$/\1/p' "$STAGE_MOD" | tr ' ' '\n' | LC_ALL=C sort
}
assert "packaged payload matches what make install copies" \
	test "$(installed_files)" = "$(packaged_files)"

# 4. A build that produced no assembly must not yield a zip at all.
d="$(build_ok partial)"
rm "$d/7dtd-fastconnect.dll"
mkdir -p "$WORK/stage-partial"
staging_fails() { ! "$STAGE_MOD" "$1" "$2" >/dev/null 2>&1; }
assert "missing assembly fails staging" \
	staging_fails "$d" "$WORK/stage-partial"
assert "failed staging leaves no staged files" \
	test -z "$(ls -A "$WORK/stage-partial")"

# 5. A missing manifest is the same failure: the game cannot load the mod.
d="$(build_ok nomanifest)"
rm "$d/ModInfo.xml"
mkdir -p "$WORK/stage-nomanifest"
assert "missing ModInfo.xml fails staging" \
	staging_fails "$d" "$WORK/stage-nomanifest"

# 6. A build output that was never produced is a setup error, not a silent
#    empty package.
mkdir -p "$WORK/stage-nobuild"
assert "absent build output fails staging" \
	staging_fails "$WORK/does-not-exist" "$WORK/stage-nobuild"

finish
