#!/usr/bin/env bash
# Shared plumbing for the offline gate scripts: PASS/FAIL accounting and the
# final RESULT line. Source this, call assert per check, finish at the end.
# Not executable by itself.

FAILS=0

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
