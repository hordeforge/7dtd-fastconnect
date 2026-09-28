#!/usr/bin/env bash
# Shared plumbing for the offline gate scripts: PASS/FAIL accounting and the
# final RESULT line. Source this, call assert per check, finish at the end.
# Not executable by itself.

FAILS=0

# Disposable working dir for a gate, under the repo's gitignored .scratch/.
# Deliberately not /tmp or $TMPDIR: the repo rule is that scratch stays in
# .scratch/ and never on a tmpfs, and a worktree-local dir also keeps the
# artifacts of a failed gate next to the tree that produced them. Callers own
# the cleanup trap, as they did with mktemp.
scratch_mktemp() {
	local root="${1:?scratch_mktemp: repo root required}"
	local prefix="${2:?scratch_mktemp: name prefix required}"
	mkdir -p "$root/.scratch"
	mktemp -d "$root/.scratch/$prefix.XXXXXX"
}

assert() {
	local name="$1"
	shift
	if "$@"; then echo "PASS $name"
	else echo "FAIL $name" >&2; FAILS=$((FAILS + 1)); fi
}

# The inverse of assert, for the gates whose subject is a refusal: a tool
# check that must reject a missing input, a validator that must exit 2. Same
# PASS/FAIL accounting, opposite expectation.
assert_fails() {
	local name="$1" rc=0
	shift
	"$@" || rc=$?
	# 126/127 mean the command under test never ran (not executable, or not
	# found), which is not a refusal. Reading those as a pass would let a
	# renamed or deleted script report green for every case that gates on it.
	if ((rc == 126 || rc == 127)); then
		echo "FAIL $name" >&2
		echo "FAIL: '$1' did not run (status $rc), so nothing was refused" >&2
		FAILS=$((FAILS + 1))
	elif ((rc == 0)); then
		echo "FAIL $name" >&2
		FAILS=$((FAILS + 1))
	else
		echo "PASS $name"
	fi
}

# assert can only test success, so "this text must NOT appear in this file"
# needs a predicate of its own. Literal and ERE variants, same argument order.
not_grep() { ! grep -q -- "$1" "$2"; }
not_grep_re() { ! grep -Eq -- "$1" "$2"; }

# Runs a command with its output discarded and yields its exit status, so a
# gate can assert on the status under set -e. Same shape in every gate that
# checks one, so it lives here once. The status lands in a global rather than
# a command substitution: a substitution runs the probe in a subshell, where
# the nonzero return aborts before the caller reads it.
RUN_RC=0
run_rc() {
	set +e
	"$@" >/dev/null 2>&1
	# shellcheck disable=SC2034  # read by the gates that source this file
	RUN_RC=$?
	set -e
}

# Audio-helper fixtures, shared by the mute/unmute gates so the two exercise
# the same stub and the same stream list. install_pactl_stub needs an existing
# dir that is ahead of the real pactl on the helper's PATH.
install_pactl_stub() {
	cp "$ROOT/scripts/testdata/pactl_stub.sh" "$1/pactl"
	chmod +x "$1/pactl"
}

# Seeded generator for the fuzz gates. A failing seed must reproduce offline,
# and urandom would make CI failures irreproducible, so both fuzz gates draw
# from one LCG here rather than each carrying its own copy that could drift.
#
# The result lands in RND rather than on stdout, because a command substitution
# per draw is a subshell fork and a generator draws hundreds of times per
# iteration.
#
# Draw from bits 8..30, not the low bits. An LCG with a power-of-two modulus
# cycles its low k bits with period 2^k, so `RAND_STATE % 4` walks 1,2,3,0
# forever and `% 2` alternates 1,0: a bound that divides the modulus would
# hand back the stream's period instead of the seed, and the generator would
# walk one fixed schedule rather than the varied one the seed exists to give.
# The high bits carry the full period; the residue is still a modulo draw, so
# it stays biased, which is immaterial for picking a fixture and would not be
# for anything security-shaped.
readonly FUZZ_SEED=20260928
RAND_STATE=$FUZZ_SEED
RND=0
readonly RAND_DRAW_SHIFT=8
rnd() {
	RAND_STATE=$(( (RAND_STATE * 1103515245 + 12345) & 0x7fffffff ))
	# shellcheck disable=SC2034  # read by the fuzz gates that source this file
	RND=$(( (RAND_STATE >> RAND_DRAW_SHIFT) % $1 ))
}

# Pins the draw above: a low-bit draw would repeat draws 1..4 at 5..8, so the
# run would explore one schedule per seed. Own copy of the state, so the run's
# stream is the seed's and this check changes nothing it draws.
rng_draw_not_short_cycle() {
	local state=$FUZZ_SEED
	# A name no sourcing gate reuses: shellcheck reads a shared local as the
	# caller's own, and a generic name here turns every gate's `local` into a
	# same-name reuse it then warns about.
	local -a draws4=()
	local i
	for ((i = 0; i < 12; i++)); do
		state=$(( (state * 1103515245 + 12345) & 0x7fffffff ))
		draws4+=("$(( (state >> RAND_DRAW_SHIFT) % 4 ))")
	done
	for ((i = 0; i < 4; i++)); do
		[[ "${draws4[i]}" != "${draws4[i + 4]}" ]] || return 1
	done
	return 0
}
assert "generator draws are not a low-bit cycle" rng_draw_not_short_cycle

# Violation counter for the fuzz gates: an invariant that breaks fires on every
# iteration, and a flood would bury the value that broke it.
VIOLATIONS=0
report() {
	local key="$1" detail="$2"
	if (( VIOLATIONS < 20 )); then
		VIOLATIONS=$(( VIOLATIONS + 1 ))
		echo "FAIL $key: $detail" >&2
	fi
}

# One stream per matching rule (application.name, case-folded binary) next to
# an unrelated one that must never be muted.
write_audio_streams() {
	printf '%s\n' '[
	 {"index": 7, "properties": {"application.name": "7DaysToDie"}},
	 {"index": 9, "properties": {"application.name": "spotify"}},
	 {"index": 11, "properties": {"application.process.binary": "7daystodie.exe"}}
	]' >"$1"
}

finish() {
	if ((FAILS > 0)); then
		echo "RESULT FAIL ($FAILS)" >&2
		exit 1
	fi
	echo "RESULT PASS"
	exit 0
}
