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

# The count cap must not lean on a tool that only exists on GNU. find -printf
# and `head -n -20` are both absent on BSD/macOS, and the 2>/dev/null in the
# old pipeline swallowed the find error, so the prune reported success while
# removing nothing.
no_gnu_only_prune() {
	! grep -vE '^[[:space:]]*#' "$src" | grep -qE 'find .*-printf|head -n -[0-9]'
}

# Runs the real prune function against a scratch tree, extracted from the
# script the way test_one_shot_launcher_group.sh extracts its functions: the
# script itself launches and kills real clients, but the prune is plain file
# work and is worth driving for real rather than pattern-matching.
prune_keeps_newest() {
	local fn i f
	WORK="$(scratch_mktemp "$ROOT" 7dtd-prune)"
	trap 'rm -rf "$WORK"' EXIT
	fn="$WORK/prune.sh"
	sed -n '/^prune_old_artifacts() {/,/^}/p' "$src" >"$fn"
	[[ -s "$fn" ]] || return 1
	SCRATCH="$WORK/cache"
	# shellcheck disable=SC2034  # read by prune_old_artifacts once it is sourced
	CYCLE_KEEP=3
	mkdir -p "$SCRATCH"
	# Three distinct mtimes, oldest last, so the order find returns them in
	# cannot decide which files survive.
	for i in 1 2 3 4 5; do
		f="$SCRATCH/launch-$i.log"
		printf 'x' >"$f"
	done
	touch -t 20200101000"$i" "$SCRATCH/launch-1.log" "$SCRATCH/launch-2.log"
	touch -t 20200101000"$((i + 1))" "$SCRATCH/launch-3.log"
	touch -t 20200101000"$((i + 2))" "$SCRATCH/launch-4.log"
	touch -t 20200101000"$((i + 3))" "$SCRATCH/launch-5.log"
	# shellcheck source=/dev/null
	source "$fn"
	prune_old_artifacts 'launch-*.log'
	local left
	left="$(find "$SCRATCH" -maxdepth 1 -type f -name 'launch-*.log' | wc -l | tr -d '[:space:]')"
	[[ "$left" == 3 ]] || return 1
	# The three newest by mtime are 3, 4, 5.
	for i in 3 4 5; do
		[[ -f "$SCRATCH/launch-$i.log" ]] || return 1
	done
	for i in 1 2; do
		[[ ! -e "$SCRATCH/launch-$i.log" ]] || return 1
	done
}

# Every per-cycle artifact the script writes into SCRATCH must be in the
# single CYCLE_ARTIFACTS list both prune rules read, or repeated cycles with a
# fresh CYCLE accumulate server logs forever. Keep the list in sync when
# adding an output.
prune_covers_all_writes() {
	local pat
	for pat in 'stock-join-*.log' 'launch-*.log' 'client-lifecycle-*.txt' 'zdtd-server-*.log'; do
		grep -qF -- "'$pat'" "$src" || return 1
	done
	# Both prune rules must consume that one list, not restate the patterns.
	[[ "$(grep -c 'CYCLE_ARTIFACTS=' "$src")" -eq 1 ]] &&
		[[ "$(grep -oc 'for pat in "\${CYCLE_ARTIFACTS\[@\]}"' "$src")" -eq 2 ]]
}

assert "one_shot_join.sh guards CYCLE before filename use" cycle_guard
assert "one_shot_join.sh prunes without word-splitting expansion" no_unquoted_prune
assert "prune reads candidates as quoted lines" grep -qE 'IFS= read -r( -d ..)? f' "$src"
assert "prune avoids find -printf / negative head" no_gnu_only_prune
assert "prune keeps the newest files of a pattern" prune_keeps_newest
assert "prune patterns cover every SCRATCH artifact incl. zdtd-server logs" prune_covers_all_writes

finish
