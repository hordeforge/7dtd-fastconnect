#!/usr/bin/env bash
# Behavioral gate for scripts/check_game_root.sh, the preflight `make build`
# runs so a missing client install is named instead of surfacing as one CS0246
# per game type the mod touches. The install is faked in scratch: the check
# only stats paths, so a tree of empty files is enough to exercise both the
# complete and the incomplete branch on a machine that has the game.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CHECK="$ROOT/scripts/check_game_root.sh"
source "$ROOT/scripts/test_common.sh"

WORK="$(scratch_mktemp "$ROOT" game-root)"
trap 'rm -rf "$WORK"' EXIT

# rc_is: assert-style predicate that succeeds only when the command exits with
# the given code. assert can only test success, so the failure statuses are
# asserted through this.
rc_is() {
	local want="$1"
	shift
	set +e
	"$@" >/dev/null 2>&1
	local rc=$?
	set -e
	((rc == want))
}

GAME_DIR="$WORK/7 Days To Die"
MANAGED="$GAME_DIR/7DaysToDie_Data/Managed"
HARMONY_DIR="$GAME_DIR/Mods/0_TFP_Harmony"

make_install() {
	rm -rf "$GAME_DIR"
	mkdir -p "$MANAGED" "$HARMONY_DIR"
	: >"$HARMONY_DIR/0Harmony.dll"
	local name
	for name in Assembly-CSharp UnityEngine.CoreModule LogLibrary \
		com.rlabrecque.steamworks.net; do
		: >"$MANAGED/$name.dll"
	done
}

assert "--help exits 0" "$CHECK" --help
assert "-h exits 0" "$CHECK" -h
assert "--help prints a usage line to stdout" \
	grep -q '^Usage:' <("$CHECK" --help)
assert "an unexpected argument exits 2" rc_is 2 "$CHECK" --nope
# stderr only, through a file: a `{ ...; }` group cannot start a command after
# a line continuation, and the 2>&1-then-1>/dev/null order trips SC2069.
usage_err() { "$CHECK" --nope 2>"$WORK/usage.err"; }
usage_err_has_usage() { usage_err; grep -q '^Usage:' "$WORK/usage.err"; }
assert "the usage error prints usage on stderr" usage_err_has_usage

# A `!` prefix cannot be an assert argument (assert runs "$@"), so the
# negative cases get a named predicate the same way test_common.sh does.
absent() { ! grep -q "$1" <<<"$2"; }

make_install
assert "a complete install passes" "$CHECK" "$GAME_DIR"
assert "a complete install reports each assembly" \
	grep -q "Assembly-CSharp.dll" <("$CHECK" "$GAME_DIR")

# A GAME env with no argument is the same root, since that is how the
# Makefile passes it: the csproj and the check must not read two defaults.
assert "GAME env is the default root" env GAME="$GAME_DIR" "$CHECK"
assert "the argument overrides GAME" env GAME="$WORK/absent" "$CHECK" "$GAME_DIR"

# The failure the preflight exists for: an install path that is not there.
missing_out="$("$CHECK" "$WORK/absent" 2>&1 || true)"
assert "a missing install exits 1" rc_is 1 "$CHECK" "$WORK/absent"
assert "a missing install names the path" grep -q "$WORK/absent" <<<"$missing_out"
assert "a missing install names the GAME= override" \
	grep -q 'GAME=' <<<"$missing_out"
assert "a missing install points at make test" \
	grep -q 'make test' <<<"$missing_out"

# Incomplete: the install is there, the assemblies are not.
rm -rf "$GAME_DIR"
mkdir -p "$GAME_DIR"
incomplete_out="$("$CHECK" "$GAME_DIR" 2>&1 || true)"
assert "an install without the assemblies exits 1" rc_is 1 "$CHECK" "$GAME_DIR"
assert "it names Assembly-CSharp.dll" grep -q 'Assembly-CSharp.dll' <<<"$incomplete_out"
assert "it names the 0_TFP_Harmony path" grep -q '0Harmony.dll' <<<"$incomplete_out"

# Present but partial: one reference removed from an otherwise good install
# must still fail, so a check that only tested the directory cannot pass here.
make_install
rm -f "$MANAGED/LogLibrary.dll"
partial_out="$("$CHECK" "$GAME_DIR" 2>&1 || true)"
assert "one missing assembly exits 1" rc_is 1 "$CHECK" "$GAME_DIR"
assert "it names the missing assembly" grep -q 'LogLibrary.dll' <<<"$partial_out"
assert "it does not report the ones present as missing" \
	absent 'missing .*Assembly-CSharp' "$partial_out"

# The Makefile has to run it, or the check is advice nobody reads.
assert "make build runs the preflight" \
	grep -q 'check_game_root.sh' "$ROOT/Makefile"

finish
