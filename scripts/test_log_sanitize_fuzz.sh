#!/usr/bin/env bash
# Fuzz gate for scripts/log_sanitize.sh, the shell twin of
# LogText.SanitizeForLog. The values it flattens are untrusted: a 7dtd log
# line carries a server-supplied world name, chat echo or version string, and
# 7DTD_CONNECT / CYCLE come from a clicked steam://run URL. The output is
# greped for fixed markers ("result=", "=== cycle" headers) by the join
# tooling, so a value that survives with a line break or an invisible format
# character in it forges a marker that a real join never wrote.
#
# The C# twin is fuzzed over a seeded grammar (test_connect_target_parse.sh);
# this gate covers the shell side, which ships the same contract in a language
# where character ranges and byte ranges disagree, and where the unit gate
# (test_log_sanitize.sh) only pins a fixed table of cases.
#
# The sanitizer's own contract is the oracle, checked as an invariant on every
# generated value: nothing line-breaking or invisible survives, a value never
# grows, a value already flat is a fixed point, and safe text passes through
# byte-identical. On top of that the value is written the way
# scripts/join_evidence.sh writes it and the written log is grepped, so the
# marker-forging claim is asserted on the artifact a reader would grep rather
# than on the helper alone.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"
source "$ROOT/scripts/log_sanitize.sh"

WORK="$(scratch_mktemp "$ROOT" 7dtd-log-sanitize-fuzz)"
trap 'rm -rf "$WORK"' EXIT

# The helper runs in this shell's locale, not a forced one: it indexes its
# code-point table by character, which is the contract log_sanitize.sh is
# written for and test_log_sanitize.sh pins. The generated values carry
# invalid UTF-8, so the greps that read them pin LC_ALL=C, where a
# fixed-string match is a statement about bytes rather than about characters.

# Each value is checked as a file, not only as a shell variable: command
# substitution strips trailing newlines, which would hide exactly the defect
# this gate exists to catch (a value that kept its line break).
IN="$WORK/in"
OUT="$WORK/out"
OUT2="$WORK/out2"
LIFE="$WORK/control.log"

# Deterministic 31-bit LCG: a failing seed must reproduce offline, and urandom
# would make CI failures irreproducible. The result lands in RND rather than on
# stdout, because a command substitution per draw is a subshell fork and the
# generator draws hundreds of times per iteration.
RAND_STATE=20260928
RND=0
rnd() {
	RAND_STATE=$(( (RAND_STATE * 1103515245 + 12345) & 0x7fffffff ))
	RND=$(( RAND_STATE % $1 ))
}

# Plain values, the shape the helper is mostly handed.
HOSTS=(
	'127.0.0.1:27025'
	'steam://connect/10.0.0.9:26900'
	'zdtd.lan'
	'1.2.3.4:1'
	''
)
# The markers the join tooling greps for, so a value carrying one is the case
# that matters: the generated value forges a marker if any line break or
# invisible character survives in front of or inside it.
MARKERS=(
	'result=joined'
	'=== one_shot_join cycle=1 connect=1.2.3.4:27025 ==='
	'key client lines:'
	'  log: '
	'ERR [7dtd-fastconnect] spawn gate still closed'
)
# Multi-byte text that must reach the log byte-identical. A byte-range tr that
# swallowed the 0xC2 lead byte of ordinary accented text would eat these, and
# the C# twin's rule is that it does not.
SAFE=(
	$'caf\u00e9.lan:27025'
	$'\u4e2d\u6587\u670d\u52a1\u5668'
	$'\360\237\230\200'
	$'na\u00efve'
	'7dtd-fastconnect'
)
# Invisible Unicode format characters, dropped by the helper. Drawn one at a
# time from the same set the helper declares, so a code point added to one and
# not the other shows up here as a surviving character.
CFORMAT=(
	$'\u200b' $'\u200c' $'\u200d' $'\u200e' $'\u200f'
	$'\u202a' $'\u202b' $'\u202c' $'\u202d' $'\u202e'
	$'\u2060' $'\u2061' $'\u2062' $'\u2063' $'\u2064'
	$'\u2066' $'\u2067' $'\u2068' $'\u2069' $'\ufeff'
)
# C0 controls and DEL, flattened to a space.
C0=(
	$'\n' $'\n' $'\r' $'\t' $'\033' $'\177' $'\001' $'\036'
)
# The two Unicode separators a log reader breaks a line on and grep does not.
SEPARATORS=($'\u2028' $'\u2029')
# Malformed or overlong encodings: a server string is not guaranteed to be
# well-formed UTF-8, and a byte-oriented rewrite of the helper would mangle
# these (or fail on them) where the shipped one passes them through. The NUL
# draw is a no-op: argv cannot carry a NUL, so the piece never reaches the
# helper, and it stays here to document that boundary.
RAW=(
	$'\xff' $'\xfe' $'\xc3' $'\xe2\x82' $'\xed\xa0\x80' $'\xc0\x80'
	$'\xf0\x9f' $'\xef\xbb' $'\x00'
)

VIOLATIONS=0
# Report once per distinct invariant: the same harness bug fires on every
# iteration and a flood would bury the actual value.
report() {
	local key="$1" detail="$2"
	if (( VIOLATIONS < 20 )); then
		VIOLATIONS=$(( VIOLATIONS + 1 ))
		echo "FAIL $key: $detail" >&2
	fi
}

# The families that ever reached a check in this run. A generator that stopped
# emitting, say, an invisible format character would make the run vacuous
# without any assertion failing.
declare -A FIRED=()
fire() { FIRED[$1]=1; }

# Forbidden output characters, in the same three groups the helper flattens.
readonly FORBIDDEN_C1_START=0x80
readonly FORBIDDEN_C1_END=0x9f

# The contract, asserted on one generated value.
check_value() {
	local val=$1 safe=$2
	local rc=0 out="" stripped size_c0 i cp hex
	local -a forbidden=("${CFORMAT[@]}" "${SEPARATORS[@]}")

	printf '%s' "$val" >"$IN"
	sanitize_log_text "$val" >"$OUT" 2>/dev/null || rc=$?
	if ((rc != 0)); then
		report "sanitizer exit status" "status $rc on $(od -An -c "$IN" | head -2 | tr -s ' ')"
		return 0
	fi
	fire sanitizer-ran
	# -d '' keeps every byte, trailing newlines included, so a value that
	# kept its line break is visible here rather than stripped away by a
	# command substitution.
	IFS= read -r -d '' out <"$OUT" || true

	# No C0 control and no DEL survives: that is what keeps one value one
	# line, and the C0 pass is the only thing that catches a byte the
	# code-point passes cannot see.
	size_c0="$(wc -c <"$OUT")"
	stripped="$(tr -d '\000-\037\177' <"$OUT" | wc -c)"
	if ((stripped != size_c0)); then
		report "no C0 or DEL in output" "$(od -An -c "$OUT" | head -2 | tr -s ' ')"
	fi

	# No invisible format character and no Unicode separator survives.
	for cp in "${forbidden[@]}"; do
		if [[ "$out" == *"$cp"* ]]; then
			report "no invisible character in output" \
				"code point survived: $(printf '%s' "$cp" | od -An -c | head -1 | tr -s ' ')"
		fi
	done

	# No C1 code point survives. C1 is a two-byte UTF-8 sequence, so a
	# byte-range rewrite would either miss it or take out every 0xC2-led
	# character, which is why the helper does it by code point.
	for ((i = FORBIDDEN_C1_START; i <= FORBIDDEN_C1_END; i++)); do
		printf -v hex '%04x' "$i"
		printf -v cp '%b' "\\u$hex"
		if [[ "$out" == *"$cp"* ]]; then
			report "no C1 code point in output" "U+$hex survived"
		fi
	done

	# Flattening removes, it never adds: a longer output than input means a
	# replacement expanded a character, which is the bug a byte-range
	# rewrite of this helper would introduce.
	if [[ "$(wc -c <"$OUT")" -gt "$(wc -c <"$IN")" ]]; then
		report "output never grows" "in=$(wc -c <"$IN") out=$(wc -c <"$OUT")"
	fi

	# A value made only of safe text is a fixed point, and it reaches the log
	# byte-identical: the C# twin passes it through too.
	if [[ "$safe" == "safe" ]]; then
		fire safe-text
		if ! cmp -s "$IN" "$OUT"; then
			report "safe text passes through byte-identical" \
				"in=$(od -An -c "$IN" | head -2 | tr -s ' ') out=$(od -An -c "$OUT" | head -2 | tr -s ' ')"
		fi
	fi

	# Idempotence: an already-flat value must be a fixed point, or a value
	# sanitized twice (evidence copied from a control log that was itself
	# sanitized) drifts.
	if [[ -n "$out" ]]; then
		sanitize_log_text "$out" >"$OUT2" 2>/dev/null || rc=$?
		if ((rc != 0)); then
			report "second pass exit status" "status $rc"
		elif ! cmp -s "$OUT" "$OUT2"; then
			report "already-flat value is a fixed point" \
				"out=$(od -An -c "$OUT" | head -2 | tr -s ' ') out2=$(od -An -c "$OUT2" | head -2 | tr -s ' ')"
		fi
	fi

	# The trust boundary, asserted on the artifact: write the value the way
	# write_join_evidence does and grep what it wrote. A value that forges a
	# marker line here is a failure a reader of the control log would see.
	if LC_ALL=C grep -qF -e 'result=' -e '===' "$OUT"; then
		fire marker-value
		: >"$LIFE"
		printf '  log: %s\n' "$out" >>"$LIFE"
		if LC_ALL=C grep -qE '^(result=|===)' "$LIFE"; then
			report "no marker line forged in the control log" \
				"line=$(grep -E '^(result=|===)' "$LIFE" | head -1)"
		fi
	fi
}

# One value: 1..6 pieces, most of them hostile. EMITTED_VALUE carries it and
# EMITTED says whether every piece was safe text, which is the only case
# allowed to pass through byte-identical. The value comes back in a global
# rather than on stdout because a command substitution would run the
# generator, and every fire in it, in a subshell.
EMITTED_VALUE=""
EMITTED=mixed
emit_value() {
	local val="" n i cp hex
	EMITTED=safe
	rnd 6
	n=$(( RND + 1 ))
	for ((i = 0; i < n; i++)); do
		rnd 10
		case "$RND" in
		0 | 1 | 2)
			rnd "${#HOSTS[@]}"
			val+="${HOSTS[RND]}"
			;;
		3)
			rnd "${#MARKERS[@]}"
			val+="${MARKERS[RND]}"
			fire marker-body
			;;
		4)
			rnd "${#SAFE[@]}"
			val+="${SAFE[RND]}"
			fire safe-body
			;;
		5)
			rnd "${#C0[@]}"
			val+="${C0[RND]}"
			fire c0
			EMITTED=mixed
			;;
		6)
			rnd "${#CFORMAT[@]}"
			val+="${CFORMAT[RND]}"
			fire cformat
			EMITTED=mixed
			;;
		7)
			rnd "${#SEPARATORS[@]}"
			val+="${SEPARATORS[RND]}"
			fire separator
			EMITTED=mixed
			;;
		8)
			rnd 32
			printf -v hex '%04x' "$(( FORBIDDEN_C1_START + RND ))"
			printf -v cp '%b' "\\u$hex"
			val+="$cp"
			fire c1
			EMITTED=mixed
			;;
		*)
			rnd "${#RAW[@]}"
			val+="${RAW[RND]}"
			fire raw-byte
			EMITTED=mixed
			;;
		esac
	done
	EMITTED_VALUE="$val"
}

ITERATIONS=${LOG_SANITIZE_FUZZ_ITERATIONS:-60}
for ((iter = 0; iter < ITERATIONS; iter++)); do
	emit_value
	check_value "$EMITTED_VALUE" "$EMITTED"
done

# The vocabulary above has to keep biting: a generator that stopped emitting a
# family, or a helper that had already flattened it, would make the whole run
# vacuous without any assertion failing.
COVERED=${#FIRED[@]}
declare -A REQUIRED=(
	[sanitizer-ran]=1
	[marker-body]=1
	[safe-body]=1
	[c0]=1
	[cformat]=1
	[separator]=1
	[c1]=1
	[raw-byte]=1
	[marker-value]=1
	[safe-text]=1
)
all_families_fired() {
	local k
	for k in "${!REQUIRED[@]}"; do
		[[ "${FIRED[$k]-}" == "${REQUIRED[$k]}" ]] || return 1
	done
	(( COVERED >= ${#REQUIRED[@]} )) || return 1
}
no_violations() { (( VIOLATIONS == 0 )); }
assert "generator reached every input family" all_families_fired
echo "fuzz: seed=20260928 iterations=$ITERATIONS values=$ITERATIONS families_fired=$COVERED violations=$VIOLATIONS"
assert "no invariant violation across the fuzz run" no_violations

finish
