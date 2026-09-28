#!/usr/bin/env bash
# Post-write verification of scripts/package.sh: a run that reports success
# must have written an archive holding exactly the staged payload. The check
# is driven through the real entry point, with the multi-minute dotnet build
# stubbed out (package.sh calls `make build`, and a hosted runner has no game
# install to compile against), and through a `zip` stub that drops an entry so
# the mismatch branch is proven to fail rather than assumed.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"
PACKAGE="$ROOT/scripts/package.sh"

if ! command -v zip >/dev/null 2>&1 || ! command -v unzip >/dev/null 2>&1; then
	echo "SKIP: zip/unzip not found; cannot test package verification" >&2
	exit 0
fi

WORK="$(scratch_mktemp "$ROOT" 7dtd-pkgverify)"
trap 'rm -rf "$WORK"' EXIT

# A stand-in repo: package.sh resolves ROOT from its own path, so the script
# under test is the real one, run against a copy of the tree that has a `make`
# which writes the payload and no dotnet.
REPO="$WORK/repo"
mkdir -p "$REPO/scripts" "$REPO/dist/7dtd-fastconnect"
cp "$PACKAGE" "$REPO/scripts/package.sh"
cp "$ROOT/scripts/stage_mod.sh" "$REPO/scripts/stage_mod.sh"
cp "$ROOT/scripts/repro_zip.sh" "$REPO/scripts/repro_zip.sh"
cat >"$REPO/Makefile" <<'STUB'
build:
	mkdir -p dist/7dtd-fastconnect
	printf 'dll' > dist/7dtd-fastconnect/7dtd-fastconnect.dll
	printf '<xml/>\n' > dist/7dtd-fastconnect/ModInfo.xml
	printf 'MIT\n' > dist/7dtd-fastconnect/LICENSE
	@echo stub-build-ok
dotnet-version:
	@echo 8.0.100
STUB

# A git repo, so package.sh's version/sha256/record steps have a HEAD to read
# (it tolerates their absence, but the archive is what is under test here).
git -C "$REPO" init -q -b main
git -C "$REPO" -c user.name=t -c user.email=t@t add -A
git -C "$REPO" -c user.name=t -c user.email=t@t commit -qm init

payload_entries() {
	unzip -Z1 "$1" | LC_ALL=C sort
}

# 1. The honest path: the run reports success and the archive holds exactly
# the three payload files under the mod folder.
VERSION=1.2.3 "$REPO/scripts/package.sh" >"$WORK/ok.log" 2>&1
ok_rc=$?
assert "a good run exits 0" test "$ok_rc" -eq 0
ok_zip="$REPO/dist/7dtd-fastconnect-1.2.3.zip"
assert "a good run names the zip after the override" test -f "$ok_zip"
assert "a good run's archive holds only the payload" \
	test "$(payload_entries "$ok_zip")" = "7dtd-fastconnect/
7dtd-fastconnect/7dtd-fastconnect.dll
7dtd-fastconnect/LICENSE
7dtd-fastconnect/ModInfo.xml"
assert "a good run writes the build record" \
	test -f "$REPO/dist/7dtd-fastconnect-1.2.3.buildinfo"
assert "the build record carries the archive digest" \
	grep -Eq '^sha256: [0-9a-f]{64}$' "$REPO/dist/7dtd-fastconnect-1.2.3.buildinfo"

# 2. An archive that lost an entry must fail the run, and must not be left
# behind: a half-correct zip that reports success is worse than no zip. The
# `zip` stub stands in front of the real one and drops ModInfo.xml, so the
# comparison is proven to catch a missing payload file rather than the code
# merely being present.
mkdir -p "$WORK/binzip"
REAL_ZIP="$(command -v zip)"
cat >"$WORK/binzip/zip" <<'STUB'
#!/usr/bin/env bash
# Drop the manifest from the entry list on stdin, then hand the real zip the
# same arguments, so the archive disagrees with the staged tree the verifier
# compares it against while the packer itself still succeeds. The real path
# comes from the gate through REAL_ZIP: naming /usr/bin/zip here would bind
# the gate to one distribution's layout.
sed '/ModInfo\.xml/d' | exec "$REAL_ZIP" "$@"
STUB
chmod +x "$WORK/binzip/zip"

set +e
PATH="$WORK/binzip:$PATH" REAL_ZIP="$REAL_ZIP" VERSION=4.5.6 "$REPO/scripts/package.sh" \
	>"$WORK/bad.log" 2>&1
bad_rc=$?
set -e
assert "an archive missing an entry fails the run" test "$bad_rc" -ne 0
assert "the mismatch names the differing entries" \
	grep -q 'ModInfo.xml' "$WORK/bad.log"
assert "a failed run leaves no archive behind" \
	test ! -e "$REPO/dist/7dtd-fastconnect-4.5.6.zip"

# 3. A build output carrying a symbol file must not leak it into the archive:
# the verification compares against the staged tree, so a leaked entry is
# caught even if staging ever regressed.
printf 'pdb' >"$REPO/dist/7dtd-fastconnect/7dtd-fastconnect.pdb"
VERSION=7.8.9 "$REPO/scripts/package.sh" >/dev/null 2>&1
assert "a build leftover does not reach the archive" \
	not_grep 'pdb' "$REPO/dist/7dtd-fastconnect-7.8.9.zip"

finish
