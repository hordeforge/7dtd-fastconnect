#!/usr/bin/env bash
# Offline gate for one_shot_join.sh's cycle-file plumbing: CYCLE reaches output
# filenames (stock-join-${CYCLE}.log and friends) and is attacker-shapable, so
# the script must validate it before interpolation, and the scratch prune must
# not expand find output unquoted into rm. The script itself is never executed
# here: it launches/kills real clients and sweeps the wine stack.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

src="$ROOT/scripts/one_shot_join.sh"

last_line_number() {
	grep -n -- "$2" "$1" | cut -d: -f1 | tail -1
}

cycle_guard() {
	local guard first_use
	guard="$(last_line_number "$src" 'WARN: CYCLE invalid')" || return 1
	first_use="$(last_line_number "$src" 'LIFE_OUT="\$SCRATCH/client-lifecycle-')" || return 1
	[[ -n "$guard" && -n "$first_use" ]] || return 1
	(( guard < first_use ))
}

no_unquoted_prune() {
	! grep -qE 'rm -f \$old' "$src"
}

# Every per-cycle artifact the script writes into SCRATCH must be covered by
# both prune rules (-mtime sweep and count cap), or repeated cycles with a
# fresh CYCLE accumulate server logs forever. The writer lines below name each
# file; keep them in sync when adding an output.
prune_covers_all_writes() {
	local pat
	for pat in 'stock-join-*.log' 'launch-*.log' 'client-lifecycle-*.txt' 'zdtd-server-*.log'; do
		grep -qF -- "'$pat'" "$src" || return 1
	done
	# The -mtime find and the count-cap loop must list the same set.
	local find_block loop_block
	find_block="$(grep -c 'zdtd-server-\*\.log' "$src")"
	loop_block="$(grep -oF "for pat in 'stock-join-*.log'" "$src" | wc -l)"
	[[ "$find_block" -eq 2 && "$loop_block" -eq 1 ]]
}

assert "one_shot_join.sh guards CYCLE before filename use" cycle_guard
assert "one_shot_join.sh prunes without word-splitting expansion" no_unquoted_prune
assert "prune reads candidates as quoted lines" grep -q 'IFS= read -r f' "$src"
assert "prune patterns cover every SCRATCH artifact incl. zdtd-server logs" prune_covers_all_writes

finish
