#!/usr/bin/env bash
# Offline gate for the two rules that keep an operator-shaped value out of a
# log another tool decides by: the control log the join harnesses grep, and
# the pkill -f pattern that acts on every process on the box.
#
# Neither script is executed here (one_shot_join.sh launches and kills real
# clients, restart_pair.sh tears down a live pair); the functions are extracted
# and driven, the way test_cycle_filename_guard.sh drives prune_old_artifacts.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"
source "$ROOT/scripts/log_sanitize.sh"

# A value shaped the way a clicked steam://run URL shapes one: a rejected
# target plus a second line carrying a marker the harness greps for.
FORGED=$'evil\nresult=joined'

extract_log_writer() {
	# Stops at the line that closes the function, whether that is the opening
	# line itself (a one-line body, the shape both harnesses have) or a "}" at
	# column 0 below it, so the block never runs on into the next function.
	awk '/^log\(\) \{/{p=1} p{print; if (/^}/ || /\{.*\}/) exit}' "$1"
}

# The writer, not its call sites: a value reaches the control log through a
# log() argument, and only the writer can guarantee every future argument is
# flattened. Both harnesses decide pass/fail by grepping their log for
# "^result=", so an unflattened newline in an echoed env value is a forged
# verdict, not a cosmetic one.
#   $1 script, $2 the file the script's log() writes to
log_writer_flattens() {
	local script="$1" dest="$2" work fn out forged
	work="$(scratch_mktemp "$ROOT" 7dtd-logwriter)"
	fn="$work/log.sh"
	extract_log_writer "$script" >"$fn"
	[[ -s "$fn" ]] || return 1
	(
		# shellcheck source=/dev/null
		source "$fn"
		# shellcheck disable=SC2034  # read by the log() sourced above
		LIFE_OUT="$work/life.txt"
		SCRATCH="$work/scratch"
		mkdir -p "$SCRATCH"
		log "missing zdtd binary: $FORGED"
	) >/dev/null
	# The flattened line is still there, and no line of it reads as a marker.
	out="$(cat "$work/$dest" 2>/dev/null || true)"
	forged="$(grep -c '^result=' "$work/$dest" 2>/dev/null || true)"
	rm -rf "$work"
	[[ "$out" == *"missing zdtd binary: evil result=joined"* ]] || return 1
	[[ "$forged" == "0" ]]
}

# ere_quote_literal feeds pkill -f, whose pattern is an ERE matched against
# every process's argv. A path carrying a metacharacter must survive as a
# literal, and a metacharacter-only value must not come back as a pattern that
# matches everything.
ere_quote_is_literal() {
	local got
	# shellcheck source=/dev/null
	source "$ROOT/scripts/proton_paths.sh"
	got="$(ere_quote_literal '/opt/my (zdtd)+/bin')"
	[[ "$got" == '/opt/my \(zdtd\)\+/bin' ]] || return 1
	got="$(ere_quote_literal '.*')"
	[[ "$got" == '\.\*' ]] || return 1
	# A plain path is unchanged, so the common case cannot stop matching.
	[[ "$(ere_quote_literal '/opt/zdtd/bin/zdtd')" == '/opt/zdtd/bin/zdtd' ]]
}

# The sweep must consume the quoted form, not the raw value: a regression that
# drops the helper call reinstates the pattern-injection.
sweep_quotes_the_pattern() {
	local src="$ROOT/scripts/restart_pair.sh"
	! grep -qE '^pkill -f "\$ZDTD"' "$src" || return 1
	grep -q 'pkill -f "\$(ere_quote_literal "\$ZDTD")"' "$src"
}

assert "one_shot_join.sh log() flattens the whole line" log_writer_flattens "$ROOT/scripts/one_shot_join.sh" life.txt
assert "zero_nre_join_loop.sh log() flattens the whole line" log_writer_flattens "$ROOT/scripts/zero_nre_join_loop.sh" scratch/zero_nre_loop.log
assert "ere_quote_literal escapes ERE metacharacters" ere_quote_is_literal
assert "restart_pair.sh sweeps with a literal-quoted pattern" sweep_quotes_the_pattern

finish
