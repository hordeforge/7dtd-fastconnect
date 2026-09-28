#!/usr/bin/env bash
# Fuzz gate for scripts/log_markers.sh, the incremental (offset-resume)
# marker scanner the join poll runs against the client log.
#
# The scanned bytes are untrusted: a 7dtd log line can carry a server-supplied
# name, level or version string, so length, encoding and line framing are all
# attacker-chapable, and the log only ever grows while the poll runs. A
# scanner that silently skips a marker reports a joined cycle that never
# happened, and a scanner that invents one reports a failure that did not
# happen, so this fuzzes the schedule (append / partial line / truncate) and
# the bytes, not just a fixed table of cases.
#
# Every query is paired with an oracle: a plain whole-file
# `grep -Eq "$re" "$LOG_MARK_FILE"` from byte zero. A fuzzer alone proves the
# presence of bugs, so each generated state must also agree with the
# unscoped scan; the harness additionally asserts the cache contract
# (idempotent, monotone, reset-on-truncate, 0/1 status only).
#
# Overlap is shrunk so the resume window is the interesting path at test
# scale, and every generated line is kept far shorter than it, so a match
# straddling a scan boundary is always inside the window and the oracle stays
# exact instead of "close enough".
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

WORK="$(scratch_mktemp "$ROOT" 7dtd-log-markers-fuzz)"
trap 'rm -rf "$WORK"' EXIT

# shellcheck disable=SC2034  # consumed by log_markers.sh via LOG_MARK_FILE
LOG_MARK_FILE="$WORK/client.log"
source "$ROOT/scripts/log_markers.sh"
# shellcheck disable=SC2034  # read by log_seen inside log_markers.sh
LOG_MARK_OVERLAP=4096

# Longest generated line, kept under LOG_MARK_OVERLAP above.
readonly MAX_LINE=256
# Log-schedule steps generated per iteration (appends, minus truncations).
readonly STEPS=2
# Each poll forks a stat plus a grep (and a tail, once the resume window is
# in play), so the budget is set where a full make test run stays reasonable;
# raise it for a long local soak. The seed is fixed, so a higher run explores
# the same prefix plus more.
readonly ITERATIONS=${MARKER_FUZZ_ITERATIONS:-60}
readonly FUZZ_SEED=20260928

# The exact marker set the join poll queries (one_shot_join.sh); a fuzz over
# a different vocabulary would not exercise the shipped decision table. The
# last entry is a deliberately backtracking shape: the scanner hands the
# pattern to grep, and a ReDoS there is a hang in the poll loop.
PATTERNS=(
	'Found own player entity with id'
	'PlayerSpawnedInWorld'
	'NET: LiteNetLib: Accepted by server'
	'EntityFactory CreateEntity: unknown type|NCSimple_Deserializer|Attempted to read past the end of the stream'
	'PlayerId\([0-9]+, [0-9]+\)'
	'Allowed ChunkViewDistance'
	'EntityFactory CreateEntity'
	'Kicked from server|NET: LiteNetLib: Disconnect|Failed to connect|connection failed'
	'never seen here (a|b)+c{0,64}'
)
# Positive bodies the generator can drop into the log, split at any point so
# a match can straddle an append boundary.
BODIES=(
	'Found own player entity with id'
	'NET: Found own player entity with id 42'
	'PlayerSpawnedInWorld'
	'Spawned in world'
	'NET: LiteNetLib: Accepted by server'
	'NET: LiteNetLib: Disconnect'
	'Kicked from server'
	'Failed to connect'
	'connection failed'
	'EntityFactory CreateEntity: unknown type'
	'EntityFactory CreateEntity: Player'
	'NCSimple_Deserializer.Read'
	'Attempted to read past the end of the stream'
	'PlayerId(1234, 0)'
	'Allowed ChunkViewDistance 24'
	'Created player entity'
	'Local Player started'
)
# Noise framing: prefixes, suffixes and byte shapes a real log carries.
FRAGMENTS=(
	'INFO NET: ' 'ERR [7dtd-fastconnect] ' 'DEBUG: ' 'WARN EXC ' '  ' '|'
	'7dtd ' 'zdtd ' 'chunk ' 'PackageIds ' 'WorldInfo '
)
# Control-byte shapes. A newline, NUL, CR, ESC or truncated UTF-8 sequence
# inside a server-supplied field is the classic way to make a line-based scan
# disagree with a substring match; the scanner must not care.
BYTES=(
	$'\n' $'\n' $'\n' $'\r' $'\0' $'\t' $'\033[2J' $'\xc3' $'\xe2\x82' $'\\n' '\\'
)

# Deterministic 31-bit LCG: a failing seed must reproduce offline, and urandom
# would make CI failures irreproducible. The result lands in RND rather than on
# stdout, because a command substitution per draw is a subshell fork and the
# generator draws hundreds of times per iteration.
RAND_STATE=$FUZZ_SEED
RND=0
rnd() {
	RAND_STATE=$(( (RAND_STATE * 1103515245 + 12345) & 0x7fffffff ))
	RND=$(( RAND_STATE % $1 ))
}

VIOLATIONS=0
# Report once per distinct invariant: the same generator bug fires on every
# iteration and a flood would bury the actual state.
report() {
	local key="$1" detail="$2"
	if (( VIOLATIONS < 20 )); then
		VIOLATIONS=$(( VIOLATIONS + 1 ))
		echo "FAIL $key: $detail" >&2
	fi
}

POLLS=0
# Patterns that ever returned 0 in this run, so a generator that stopped
# emitting a matchable body (making the whole run vacuous) is detectable.
declare -A PATTERNS_MATCHED=()
# Patterns that returned 0 during the current iteration, so monotonicity
# (a cached positive never un-matches on an append-only log) can be asserted
# across the steps that follow.
MATCHED=()

poll_all() {
	local re rc
	MATCHED=()
	for re in "${PATTERNS[@]}"; do
		set +e
		log_seen "$re"
		rc=$?
		set -e
		if (( rc != 0 && rc != 1 )); then
			report "log_seen status" "re='$re' returned $rc"
			continue
		fi
		POLLS=$(( POLLS + 1 ))
		# The cache itself must hold the same verdict it just returned.
		if [[ "${SEEN_MARK[$re]-1}" != "$rc" ]]; then
			report "cache mirrors the verdict" "re='$re' returned $rc but cached ${SEEN_MARK[$re]-<unset>}"
		fi
		if (( rc == 0 )) && [[ "${PATTERNS_MATCHED[$re]-x}" == "x" ]]; then
			PATTERNS_MATCHED[$re]=1
		fi
		if (( rc == 0 )); then
			MATCHED+=("$re")
		fi
	done
}

# Oracle: an unscoped scan of the whole file must reach the same verdict as
# the incrementally resumed scan. This is the assertion that turns a silently
# skipped marker into a test failure.
check_oracle() {
	local re got want
	for re in "${PATTERNS[@]}"; do
		if grep -Eq -- "$re" "$LOG_MARK_FILE" 2>/dev/null; then want=0; else want=1; fi
		if [[ "${SEEN_MARK[$re]-1}" == "0" ]]; then got=0; else got=1; fi
		if (( got != want )); then
			report "offset resume agrees with a full scan" \
				"re='$re' scanner=$got full-scan=$want log=$(od -c "$LOG_MARK_FILE" | head -3 | tr '\n' ' ')"
		fi
	done
}

# Second half of a body split across a poll boundary; emitted with no
# surrounding newline so the two halves are one contiguous match.
PENDING=""

emit_chunk() {
	local n i frag body half
	# A body whose first half ended the previous chunk: the marker now
	# straddles exactly the offset the next poll resumes from, which is the
	# case the overlap window exists to cover. Independent halves never form
	# a real match, so this has to be a true complement.
	if [[ -n "$PENDING" ]]; then
		printf '%s' "$PENDING" >>"$LOG_MARK_FILE"
		PENDING=""
	fi
	rnd 12
	n=$(( RND + 1 ))
	for (( i = 0; i < n; i++ )); do
		rnd 6
		case "$RND" in
			0)
				# A whole body, sometimes prefixed and suffixed so the
				# marker does not sit at a line start.
				rnd "${#FRAGMENTS[@]}"
				frag="${FRAGMENTS[RND]}"
				rnd "${#BODIES[@]}"
				frag+="${BODIES[RND]}"
				rnd 2
				if (( RND == 0 )); then
					rnd "${#FRAGMENTS[@]}"
					frag+="${FRAGMENTS[RND]}"
				fi
				;;
			1)
				# First half of a body: this is what puts a match
				# across an append boundary, the case the overlap
				# window exists for.
				rnd "${#BODIES[@]}"
				frag="${BODIES[RND]}"
				frag="${frag:0:$(( ${#frag} / 2 ))}"
				;;
			2)
				# Second half of a body, for the same reason.
				rnd "${#BODIES[@]}"
				frag="${BODIES[RND]}"
				frag="${frag:$(( ${#frag} / 2 ))}"
				;;
			3)
				# Raw byte shape, no framing.
				rnd "${#BYTES[@]}"
				frag="${BYTES[RND]}"
				;;
			*)
				rnd "${#FRAGMENTS[@]}"
				frag="${FRAGMENTS[RND]}"
				rnd "${#FRAGMENTS[@]}"
				frag+="${FRAGMENTS[RND]}"
				;;
		esac
		# Never emit a line longer than the overlap window, or the oracle
		# above is no longer entitled to disagree-free exactness.
		if (( ${#frag} > MAX_LINE )); then
			frag="${frag:0:MAX_LINE}"
		fi
		printf '%s' "$frag" >>"$LOG_MARK_FILE"
		# A trailing newline is not guaranteed in a log being written: a
		# line still in flight must not match until it is complete.
		rnd 4
		if (( RND != 0 )); then
			printf '\n' >>"$LOG_MARK_FILE"
		fi
	done
	# End the chunk mid-marker, so the next poll's resume offset falls
	# inside a body that the following chunk completes.
	rnd 3
	if (( RND == 0 )); then
		rnd "${#BODIES[@]}"
		body="${BODIES[RND]}"
		half=$(( ${#body} / 2 ))
		printf '%s' "${body:0:half}" >>"$LOG_MARK_FILE"
		PENDING="${body:half}"
	fi
}

for (( iter = 0; iter < ITERATIONS; iter++ )); do
	log_marks_reset
	PENDING=""
	: >"$LOG_MARK_FILE"
	# Half the iterations start from a non-empty file so the cold path is
	# not the only one that gets a full scan.
	if (( iter % 2 == 0 )); then
		emit_chunk
	fi
	MATCHED=()
	for (( step = 0; step < STEPS; step++ )); do
		emit_chunk
		poll_all
		# A pattern that matched in an earlier step of this iteration must
		# still match: the log only grew.
		for re in "${MATCHED[@]-}"; do
			[[ -n "$re" ]] || continue
			set +e
			log_seen "$re"
			rc=$?
			set -e
			(( rc == 0 )) || report "positive is monotone" "re='$re' un-matched after growth"
		done
	done
	check_oracle

	# A repeat query must not change the verdict (the memo is stable).
	for re in "${PATTERNS[@]}"; do
		if [[ "${SEEN_MARK[$re]-x}" == "x" ]]; then
			set +e
			log_seen "$re"
			rc_before=$?
			log_seen "$re"
			rc_after=$?
			set -e
			if (( rc_before != rc_after )); then
				report "repeat query is stable" "re='$re' $rc_before then $rc_after"
			fi
		fi
	done

	# Every fifth iteration truncates mid-stream. Truncation is a new cycle,
	# so the contract requires log_marks_reset first; the scanner must then
	# agree with a full scan of the short file again.
	if (( iter % 5 == 4 )); then
		: >"$LOG_MARK_FILE"
		emit_chunk
		emit_chunk
		log_marks_reset
		: >"$LOG_MARK_FILE"
		emit_chunk
		poll_all
		check_oracle
	fi
done

# The vocabulary above has to keep biting: a generator that stopped emitting
# a body, or a pattern that stopped being able to match one, would make the
# whole run vacuous without any assertion failing.
COVERED=${#PATTERNS_MATCHED[@]}
covered_enough() { (( COVERED >= 7 )); }
no_violations() { (( VIOLATIONS == 0 )); }
assert "generator reached most marker patterns" covered_enough
echo "fuzz: seed=$FUZZ_SEED iterations=$ITERATIONS polls=$POLLS patterns_matched=$COVERED violations=$VIOLATIONS"
assert "no invariant violation across the fuzz run" no_violations

finish
