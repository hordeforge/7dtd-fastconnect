#!/usr/bin/env bash
# Gate for scripts/coverage_badge.py: what the badge renderer does with a
# report it can read, and how it fails on one it cannot.
#
# The empty-filter case is the one that matters: a profiler run that collected
# nothing used to render a 0% badge and exit 0, so `make coverage` published a
# measurement for a run that never measured anything. That is a faked zero
# reading as a healthy one, so the renderer must refuse instead.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

# The pinned interpreter, the same one the Makefile's coverage target uses.
if command -v uv >/dev/null 2>&1; then
	PY=(uv run --locked --group dev python "$ROOT/scripts/coverage_badge.py")
else
	PY=(python3 "$ROOT/scripts/coverage_badge.py")
fi

WORK="$(scratch_mktemp "$ROOT" cov-badge)"
trap 'rm -rf "$WORK"' EXIT

# Minimal Cobertura report: one hit line and one missed line under a product
# filename, one under a harness filename the filter must exclude.
cat >"$WORK/report.xml" <<-'XML'
	<?xml version="1.0"?>
	<coverage>
	  <packages>
	    <classes>
	      <class filename="/Source/ConnectMod/ConnectTarget.cs">
	        <lines>
	          <line number="10" hits="1"/>
	          <line number="11" hits="0"/>
	        </lines>
	      </class>
	      <class filename="scripts/testdata/connect_target_harness.cs">
	        <lines>
	          <line number="1" hits="0"/>
	        </lines>
	      </class>
	    </classes>
	  </packages>
	</coverage>
XML

# Runs the renderer, keeping stdout and stderr for the assertions and the
# exit status in BADGE_RC, so one run can be checked for status and for the
# reason it gave.
BADGE_RC=0
run_badge() {
	set +e
	"${PY[@]}" "$@" >"$WORK/out" 2>"$WORK/err"
	BADGE_RC=$?
	set -e
}

# Succeeds only when the last run_badge exited <want>.
badge_rc_is() {
	((BADGE_RC == $1))
}

# is_status <want> <cmd...>: succeeds only when the command exited <want>, for
# the runs whose output nothing here asserts on.
is_status() {
	local want="$1"
	shift
	set +e
	"$@" >/dev/null 2>&1
	local rc=$?
	set -e
	((rc == want))
}

assert "a report with product lines writes the badge" \
	run_badge "$WORK/badge.svg" "/Source/" "$WORK/report.xml"
assert "the badge carries the hit rate of the product lines" \
	grep -q 'coverage: 50%' "$WORK/badge.svg"
assert "harness lines stay out of the denominator" \
	not_grep 'coverage: 0%' "$WORK/badge.svg"

# Nothing matched the filter: no coverage was collected, which is not a
# measurement of zero. The renderer must say so and fail.
run_badge "$WORK/empty.svg" "/NoSuchPath/" "$WORK/report.xml"
assert "an empty filter match exits 1" badge_rc_is 1
# The reason comes from the capture the run above left behind.
assert "an empty filter match names the filter" \
	grep -q 'no line of any report matched' "$WORK/err"
assert "an empty filter match writes no badge" \
	test ! -e "$WORK/empty.svg"

# A report that cannot be parsed is the same class of failure and must not be
# swallowed into a rendered number.
printf 'not xml at all\n' >"$WORK/broken.xml"
assert "an unparsable report exits 1" \
	is_status 1 "${PY[@]}" "$WORK/broken.svg" "/Source/" "$WORK/broken.xml"
assert "an unreadable report exits 1" \
	is_status 1 "${PY[@]}" "$WORK/missing.svg" "/Source/" "$WORK/absent.xml"
assert "an unparsable report writes no badge" \
	test ! -e "$WORK/broken.svg"

finish
