#!/usr/bin/env bash
# Offline gate for zero_nre_join_loop.sh's verdict files: a run must start from
# no verdict, so the PASS left by an earlier run cannot outlive it. Only the
# branch that produces a verdict writes one, so a later failing run writes
# FAIL.txt and would otherwise sit next to a stale PASS.txt from a run that
# passed, with nothing on disk to say which run each came from.
# The script itself is never executed here: it launches/kills real clients and
# servers and sweeps processes by name.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

src="$ROOT/scripts/zero_nre_join_loop.sh"

first_line_number() {
	grep -Fn -- "$2" "$1" | cut -d: -f1 | head -1
}

verdicts_cleared_before_writing() {
	local reset_line pass_line fail_line
	reset_line="$(first_line_number "$src" 'rm -f "$SCRATCH/zero_nre_PASS.txt" "$SCRATCH/zero_nre_FAIL.txt"')"
	pass_line="$(first_line_number "$src" 'tee "$SCRATCH/zero_nre_PASS.txt"')"
	fail_line="$(first_line_number "$src" 'tee "$SCRATCH/zero_nre_FAIL.txt"')"
	[[ -n "$reset_line" && -n "$pass_line" && -n "$fail_line" ]] || return 1
	(( reset_line < pass_line && reset_line < fail_line ))
}

assert "zero_nre_join_loop.sh clears both verdicts before writing either" \
	verdicts_cleared_before_writing

finish
