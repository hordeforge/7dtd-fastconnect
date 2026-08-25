#!/usr/bin/env bash
# Byte-reproducibility contract of scripts/repro_zip.sh: identical inputs that
# differ only in wall-clock mtimes must zip to identical bytes, and
# SOURCE_DATE_EPOCH must actually drive the archived timestamps.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

if ! command -v zip >/dev/null 2>&1 || ! command -v unzip >/dev/null 2>&1; then
	echo "SKIP: zip/unzip not found; cannot test reproducible packaging" >&2
	exit 0
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/7dtd-reprozip.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

stage_a="$WORK/a"
stage_b="$WORK/b"
for d in "$stage_a" "$stage_b"; do
	mkdir -p "$d/7dtd-fastconnect/sub"
	printf 'dll-bytes' > "$d/7dtd-fastconnect/mod.dll"
	printf '<xml/>\n' > "$d/7dtd-fastconnect/ModInfo.xml"
	printf 'zz' > "$d/7dtd-fastconnect/sub/zz.bin"
	printf 'aa' > "$d/7dtd-fastconnect/sub/aa.bin"
done
# Different wall-clock mtimes and different creation order (so readdir order
# differs too); a deterministic packer must erase both differences.
touch -t 202001010000 "$stage_a"/7dtd-fastconnect/mod.dll \
	"$stage_a"/7dtd-fastconnect/sub/zz.bin "$stage_a"/7dtd-fastconnect/sub/aa.bin
touch -t 203305060708.09 "$stage_b"/7dtd-fastconnect/sub/aa.bin \
	"$stage_b"/7dtd-fastconnect/mod.dll \
	"$stage_b"/7dtd-fastconnect/ModInfo.xml "$stage_b"/7dtd-fastconnect/sub/zz.bin

EPOCH=1700000000 # 2023-11-14 22:13:20 UTC
SOURCE_DATE_EPOCH=$EPOCH "$ROOT/scripts/repro_zip.sh" "$stage_a" "$WORK/a.zip" >/dev/null
SOURCE_DATE_EPOCH=$EPOCH TZ=America/New_York LC_ALL=C.UTF-8 \
	"$ROOT/scripts/repro_zip.sh" "$stage_b" "$WORK/b.zip" >/dev/null
SOURCE_DATE_EPOCH=$((EPOCH + 3600)) "$ROOT/scripts/repro_zip.sh" "$stage_a" "$WORK/c.zip" >/dev/null

differ() { ! cmp -s "$1" "$2"; }

assert "identical inputs, different mtimes/order -> byte-identical zip" \
	cmp -s "$WORK/a.zip" "$WORK/b.zip"
assert "SOURCE_DATE_EPOCH flows into the archive bytes" \
	differ "$WORK/a.zip" "$WORK/c.zip"

names="$(unzip -Z1 "$WORK/a.zip")"
sorted_names="$(printf '%s\n' "$names" | LC_ALL=C sort)"
assert "entry order is explicitly sorted (dirs included)" \
	test "$names" = "$sorted_names"

first_entry_ts="$(unzip -l "$WORK/a.zip" | awk 'NR==4 {print $2, $3}')"
assert "entry mtimes follow SOURCE_DATE_EPOCH ($first_entry_ts)" \
	test "$first_entry_ts" = "2023-11-14 22:13"

finish
