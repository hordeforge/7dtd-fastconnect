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
	printf 'MIT\n' >"$d/LICENSE"
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
assert "staged tree holds only the payload files" \
	test "$entries" = "7dtd-fastconnect/
7dtd-fastconnect/7dtd-fastconnect.dll
7dtd-fastconnect/LICENSE
7dtd-fastconnect/ModInfo.xml"

# 2. The zip's top level is the mod folder, so unzipping into <game>/Mods
#    installs it where the game loader looks.
assert "zip has the mod folder at its top level" \
	test "$(printf '%s\n' "$entries" | head -1)" = "7dtd-fastconnect/"

# 3. The installed file set and the packaged one are the same set: a user
#    who `make install`s and a user who unzips a release get the same mod.
#    make install copies both files in one cp -f of "$(DIST)/A" "$(DIST)/B".
# Every file make install copies out of the build dir. The trailing filter
# drops `"$(DIST)/"`, the build target directory, which is not a payload file.
installed_files() {
	grep -o '"\$(DIST)/[^"]*"' "$ROOT/Makefile" | sed 's/"\$(DIST)\///; s/"$//' \
		| grep -v '^$' | LC_ALL=C sort
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

# 7. Staging again into a stage root a previous run used converges on the
#    payload. The staged tree drives the shipped zip, so a file an earlier
#    stage (or a half-finished run) left in the mod folder would ship and make
#    the artifact depend on the stage root's history instead of on the build.
d="$(build_ok rerun)"
mkdir -p "$WORK/stage-rerun"
"$STAGE_MOD" "$d" "$WORK/stage-rerun"
printf 'pdb' >"$WORK/stage-rerun/7dtd-fastconnect/7dtd-fastconnect.pdb"
printf 'old' >"$WORK/stage-rerun/7dtd-fastconnect/7dtd-fastconnect.old.dll"
mkdir -p "$WORK/stage-rerun/7dtd-fastconnect/sub"
printf 'stray' >"$WORK/stage-rerun/7dtd-fastconnect/sub/stray.bin"
"$STAGE_MOD" "$d" "$WORK/stage-rerun"
assert "staging into a used stage root yields exactly the payload" \
	test "$(find "$WORK/stage-rerun" -mindepth 1 | sed "s|$WORK/stage-rerun/||" | LC_ALL=C sort)" = \
		"7dtd-fastconnect
7dtd-fastconnect/7dtd-fastconnect.dll
7dtd-fastconnect/LICENSE
7dtd-fastconnect/ModInfo.xml"

# 8. `make install` and `make uninstall` act on $(INSTALL_DIR), which an empty
#    or root MODS_DIR collapses to a top-level path that uninstall would
#    delete recursively. The guard must reject those before any disk change
#    and let a real Mods dir through.
guard() { make -C "$ROOT" --no-print-directory check-mods-dir MODS_DIR="$1" >/dev/null 2>&1; }
guard_fails() { ! guard "$1"; }
assert "install guard rejects an empty MODS_DIR" guard_fails ""
assert "install guard rejects a root MODS_DIR" guard_fails "/"
assert "install guard accepts a game Mods dir" guard "$WORK/game/Mods"

finish
