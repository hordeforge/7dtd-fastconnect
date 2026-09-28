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
	local name="$1"
	shift
	if "$@"; then echo "FAIL $name" >&2; FAILS=$((FAILS + 1))
	else echo "PASS $name"; fi
}

# assert can only test success, so "this text must NOT appear in this file"
# needs a predicate of its own. Literal and ERE variants, same argument order.
not_grep() { ! grep -q -- "$1" "$2"; }
not_grep_re() { ! grep -Eq -- "$1" "$2"; }

# Audio-helper fixtures, shared by the mute/unmute gates so the two exercise
# the same stub and the same stream list. install_pactl_stub needs an existing
# dir that is ahead of the real pactl on the helper's PATH.
install_pactl_stub() {
	cp "$ROOT/scripts/testdata/pactl_stub.sh" "$1/pactl"
	chmod +x "$1/pactl"
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
