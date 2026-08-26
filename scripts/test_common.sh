#!/usr/bin/env bash
# Shared plumbing for the offline gate scripts: PASS/FAIL accounting and the
# final RESULT line. Source this, call assert per check, finish at the end.
# Not executable by itself.

FAILS=0

# Disposable working dir for a gate, under the repo's gitignored .scratch/.
# Deliberately not /tmp or $TMPDIR: those are tmpfs on this platform, so every
# staged game tree and zip fixture a gate builds is charged to RAM and lost on
# reboot. Callers own the cleanup trap, as they did with mktemp.
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

finish() {
	if ((FAILS > 0)); then
		echo "RESULT FAIL ($FAILS)" >&2
		exit 1
	fi
	echo "RESULT PASS"
	exit 0
}
