#!/usr/bin/env bash
# Behavioral test for the shared harness value checks
# (scripts/config_validate.sh), plus the wiring that keeps every join script
# using them: a PORT the client could never join must be rejected where it is
# read, not surface later as a listen or join timeout.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"
source "$ROOT/scripts/config_validate.sh"

valid_port() { is_tcp_port "$1"; }
# assert takes a command whose status decides the check; a bare `! is_tcp_port
# x` would trip set -e's own handling of the failing condition.
rejects() { if is_tcp_port "$1"; then return 1; fi; }

assert "accepts the default port" valid_port 27025
assert "accepts the range bounds" valid_port 1
assert "accepts the top of the range" valid_port 65535
assert "accepts a leading zero as decimal" valid_port 027025
assert "rejects port 0" rejects 0
assert "rejects a port above the range" rejects 65536
assert "rejects non-numeric text" rejects 27a25
assert "rejects an empty value" rejects ''
assert "rejects a value with metacharacters" rejects '27025|ls'

for f in one_shot_join.sh zero_nre_join_loop.sh restart_pair.sh; do
	assert "$f sources the shared checks" grep -q 'config_validate.sh' "$ROOT/scripts/$f"
	assert "$f validates PORT through is_tcp_port" grep -q 'if ! is_tcp_port "$PORT"' "$ROOT/scripts/$f"
done

# env_bool is the shell twin of the mod's EnvFlags, so it has to answer for the
# same tokens in the same direction. Each probe prints the parsed value, so a
# `!` predicate would trip set -e instead of deciding the assert.
bool_of() { env_bool "TEST_BOOL=$1" 0 2>/dev/null; }
bool_is() { [[ "$(bool_of "$1")" == "$2" ]]; }
opt_in() { bool_is "$1" 1; }
opt_out() { bool_is "$1" 0; }
blank_keeps_default() { [[ "$(env_bool "TEST_BOOL=$1" 1)" == 1 ]]; }
alias_wins_when_primary_blank() {
	[[ "$(env_bool "TEST_BOOL=$1" "TEST_ALIAS=$2" 0 2>/dev/null)" == "$3" ]]
}
# No such line: a knob that keeps its own boolean case is a second table that
# can drift from the shared one.
no_grep() { if grep -q "$1" "$2"; then return 1; fi; }

assert "opt-in tokens" opt_in 1
assert "opt-in true" opt_in true
assert "opt-in yes" opt_in yes
assert "opt-in on" opt_in on
assert "opt-in is case-folded" opt_in ON
assert "opt-out zero" opt_out 0
assert "opt-out false" opt_out false
assert "opt-out no" opt_out no
assert "opt-out off" opt_out off
assert "opt-out is trimmed" opt_out ' off '
assert "blank keeps the default" blank_keeps_default ''
assert "whitespace-only keeps the default" blank_keeps_default ' '
assert "an undocumented token reads as on" opt_in ture
assert "a blank primary falls through to the alias" alias_wins_when_primary_blank '' 1 1
assert "a set primary wins over the alias" alias_wins_when_primary_blank 0 1 0
assert "an undocumented token warns naming the variable" \
	grep -q "TEST_BOOL='ture' is not a documented boolean" \
	<(env_bool "TEST_BOOL=ture" 0 2>&1 >/dev/null)

# The warning is what keeps a typo from reading as a deliberate setting, so
# the knobs that gate a side effect have to go through the shared reader.
assert "one_shot_join.sh reads START_SERVER through env_bool" \
	grep -q 'START_SERVER="$(env_bool "START_SERVER=${START_SERVER-}" 0)"' \
	"$ROOT/scripts/one_shot_join.sh"
assert "launch_client.sh reads CLIENT_MUTE through env_bool" \
	grep -q 'env_bool' "$ROOT/scripts/launch_client.sh"
assert "launch_client.sh keeps no private boolean case" \
	no_grep 'MUTE_CLIENT="1" ;;' "$ROOT/scripts/launch_client.sh"
# The normalizers the boolean reader needs live in the shared file, so an enum
# trimmed in one script is trimmed the same way in the other.
assert "launch_client.sh sources the shared normalizers" \
	grep -q 'source "$SCRIPT_DIR/config_validate.sh"' "$ROOT/scripts/launch_client.sh"
assert "launch_client.sh keeps no second copy of trim" \
	no_grep '^trim() {' "$ROOT/scripts/launch_client.sh"

finish
