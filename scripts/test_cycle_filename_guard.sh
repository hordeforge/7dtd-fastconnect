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

assert "one_shot_join.sh guards CYCLE before filename use" cycle_guard
assert "one_shot_join.sh prunes without word-splitting expansion" no_unquoted_prune
assert "prune reads candidates as quoted lines" grep -q 'IFS= read -r f' "$src"

finish
