#!/usr/bin/env bash
# Offline gate for zero_nre_join_loop.sh's client-log plumbing: every attempt
# truncates the client log inside the Proton prefix, so the guest logs dir must
# exist before the first truncation or a fresh prefix (first run, custom
# COMPAT, second-disk library) aborts under set -e before any attempt starts.
# The script itself is never executed here: it launches/kills real clients and
# servers and sweeps processes by name.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

src="$ROOT/scripts/zero_nre_join_loop.sh"

first_line_number() {
	grep -Fn -- "$2" "$1" | cut -d: -f1 | head -1
}

mkdir_before_truncate() {
	local mkdir_line trunc_line
	mkdir_line="$(first_line_number "$src" 'mkdir -p "$(dirname "$CLIENT_LOG_SRC")"')"
	trunc_line="$(first_line_number "$src" ': >"$CLIENT_LOG_SRC"')"
	[[ -n "$mkdir_line" && -n "$trunc_line" ]] || return 1
	(( mkdir_line < trunc_line ))
}

assert "zero_nre_join_loop.sh creates the client log dir before truncating it" mkdir_before_truncate

finish
