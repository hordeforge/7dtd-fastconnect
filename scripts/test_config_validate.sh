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

# The unbounded knobs have no range of their own, only the one the arithmetic
# downstream can hold, so the table pins the ends of that.
valid_count() { is_bounded_uint "$1"; }
rejects_count() { if is_bounded_uint "$1"; then return 1; fi; }

assert "accepts a zero count (SETTLE_SEC's default)" valid_count 0
assert "accepts a count with leading zeros" valid_count 000000000000000090
assert "accepts the longest value the arithmetic holds" valid_count 999999999999999999
assert "rejects a value that wraps to a small one" rejects_count 18446744073709551617
assert "rejects a non-ASCII digit" rejects_count '٢٧٠٢٥'
assert "rejects a negative count" rejects_count -1

for f in one_shot_join.sh zero_nre_join_loop.sh restart_pair.sh; do
	assert "$f sources the shared checks" grep -q 'config_validate.sh' "$ROOT/scripts/$f"
	assert "$f validates PORT through is_tcp_port" grep -q 'if ! is_tcp_port "$PORT"' "$ROOT/scripts/$f"
done
# The seconds and attempt knobs reach $(( )) arithmetic and sleep(1) in both
# join scripts, so each one is validated through the shared check rather than
# through a copy of the digit test.
for knob in TIMEOUT_SEC SETTLE_SEC; do
	assert "one_shot_join.sh validates $knob through is_bounded_uint" \
		grep -q "if ! is_bounded_uint \"\$$knob\"" "$ROOT/scripts/one_shot_join.sh"
done
for knob in TIMEOUT_SEC MAX_ATTEMPTS; do
	assert "zero_nre_join_loop.sh validates $knob through is_bounded_uint" \
		grep -q "if ! is_bounded_uint \"\$$knob\"" "$ROOT/scripts/zero_nre_join_loop.sh"
done

# Seeded fuzz over the numeric knobs. They reach the harness from the
# environment, so their length and their digits are caller-controlled, and the
# check they feed decides whether a run starts a server at that port, waits
# that long, or gives up after that many cycles. A value the check accepts but
# the client refuses, or one that wraps in $(( )), costs a whole run, so each
# verdict is compared against the rule stated in the file's own header rather
# than against the implementation, which is what makes a wrap or an
# off-by-one in the bounds visible.
#
# The oracles read the digits as a string. Nothing in them evaluates
# $((10#...)), so a check that shares the implementation's arithmetic cannot
# vouch for itself.
RAND_STATE=20260928
RND=0
rnd() {
	RAND_STATE=$(( (RAND_STATE * 1103515245 + 12345) & 0x7fffffff ))
	RND=$(( RAND_STATE % $1 ))
}

# Pieces the generator splices: shapes a typo, a copy-paste and a hostile
# environment all produce.
PIECES=(
	'0' '1' '2' '7' '9' '5' '6' '8' '3' '4'
	'27025' '27015' '27020' '26999' '27100'
	'1' '65535' '65536' '65534' '0' '00000'
	'0000' '0000000000000000000000000' # a long leading-zero run
	'-1' '+1' ' ' $'\t' $'\n' $'\r'
	'0x1' '27_025' '2 7025' '27,025' '٢٧٠٢٥' # non-ASCII digits
	# 2^64 and 2^64+k: values that wrap in bash's 64-bit arithmetic, which
	# is where a range check that evaluates the number before bounding it
	# goes wrong.
	'18446744073709551616' '18446744073709551617'
	'18446744073709551615' '99999999999999999999999999'
)

# The rule, stated over the string.
oracle_accepts() {
	# C locale, as the rule is about digits: under a UTF-8 collation "9"
	# sorts after "65535", and the bound would come out inverted.
	local LC_ALL=C
	local v=$1 significant
	[[ "$v" =~ ^[0-9]+$ ]] || return 1
	significant="${v#"${v%%[!0]*}"}"
	[[ -n "$significant" && ${#significant} -le 5 ]] || return 1
	# Anything shorter than five digits is under the top of the range. A
	# five-digit value is spelled out instead of compared: the alternatives
	# are a lexical compare (which inverts under a UTF-8 collation) or the
	# same $((10#...)) the check under test uses, and an oracle that shares
	# the implementation's arithmetic cannot catch it.
	(( ${#significant} < 5 )) && return 0
	case "$significant" in
	[0-5][0-9][0-9][0-9][0-9] | 6[0-4][0-9][0-9][0-9] | 65[0-4][0-9][0-9] | 655[0-2][0-9] | 6553[0-5])
		return 0
		;;
	esac
	return 1
}

# The unbounded knobs' rule: digits only, and few enough of them that the
# $(( )) downstream cannot wrap. The length bound is the widest one that
# still fits a signed 64-bit value.
oracle_accepts_count() {
	local LC_ALL=C
	local v=$1 significant
	[[ "$v" =~ ^[0-9]+$ ]] || return 1
	significant="${v#"${v%%[!0]*}"}"
	[[ -n "$significant" ]] || significant=0
	(( ${#significant} <= 18 ))
}

VIOLATIONS=0
report() {
	local key="$1" detail="$2"
	if ((VIOLATIONS < 20)); then
		VIOLATIONS=$((VIOLATIONS + 1))
		echo "FAIL $key: $detail" >&2
	fi
}
ACCEPTED=0
REJECTED=0

# One value, checked against both checks: they read the same env knobs, and a
# value that satisfies one rule and not the other is exactly the case where a
# caller would be told the port is fine and the timeout is not.
ITERATIONS=${PORT_FUZZ_ITERATIONS:-200}
for ((iter = 0; iter < ITERATIONS; iter++)); do
	# 1..3 pieces, so the value is sometimes a bare token and sometimes a
	# good one pasted against something else.
	val=""
	rnd 3
	n=$((RND + 1))
	for ((i = 0; i < n; i++)); do
		rnd "${#PIECES[@]}"
		val+="${PIECES[RND]}"
	done
	[[ -n "$val" ]] || val=0

	for check in is_tcp_port is_bounded_uint; do
		case "$check" in
		is_tcp_port) rule_is=oracle_accepts ;;
		*) rule_is=oracle_accepts_count ;;
		esac
		rc=0
		err="$( { "$check" "$val"; } 2>&1 >/dev/null )" || rc=$?
		if [[ -n "$err" ]]; then
			report "no diagnostic on stderr" "$check value='$val' stderr='$err'"
		fi
		if ((rc != 0 && rc != 1)); then
			report "status is 0 or 1" "$check value='$val' status $rc"
			continue
		fi

		if "$rule_is" "$val"; then want=0; else want=1; fi
		if ((rc != want)); then
			report "verdict matches the stated rule" \
				"$check value='$val' status=$rc rule=$want"
		fi
		if [[ "$check" == "is_tcp_port" ]]; then
			if ((rc == 0)); then ACCEPTED=$((ACCEPTED + 1)); else REJECTED=$((REJECTED + 1)); fi
		fi
	done
done

# Both verdicts have to occur, or the run proved nothing about the bounds: a
# generator that only produced valid ports would leave the upper limit
# untested.
assert "fuzz reached both verdicts" test "$ACCEPTED" -gt 0 -a "$REJECTED" -gt 0
echo "fuzz: seed=20260928 iterations=$ITERATIONS ports_accepted=$ACCEPTED ports_rejected=$REJECTED violations=$VIOLATIONS"
assert "no invariant violation across the fuzz run" test "$VIOLATIONS" -eq 0

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
