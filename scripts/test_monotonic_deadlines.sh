#!/usr/bin/env bash
# Bounded waits must measure elapsed time on a monotonic clock (/proc/uptime,
# CLOCK_BOOTTIME), not Bash's wall-clock-derived $SECONDS: an NTP step or
# manual correction mid-wait extends or truncates timeouts, so a join cycle
# would hang past its budget or kill a client that was about to spawn.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

CLOCK="$ROOT/scripts/monotonic_clock.sh"
assert "shared clock module reads monotonic uptime" grep -q '/proc/uptime' "$CLOCK"

# shellcheck source=scripts/monotonic_clock.sh
source "$CLOCK"

mono_output_is_integer() {
	[[ "$(mono_sec)" =~ ^[0-9]+$ ]]
}

mono_reads_never_go_backwards() {
	local a b
	a="$(mono_sec)"
	b="$(mono_sec)"
	(( b >= a ))
}

# One second must advance the reading: catches a clock frozen at its first
# sample (e.g. cached), which would turn every deadline into "never expires".
mono_advances_with_time() {
	local a b
	a="$(mono_sec)"
	sleep 1
	b="$(mono_sec)"
	(( b > a ))
}

for f in scripts/mute_client_audio.sh scripts/one_shot_join.sh; do
	assert "$f sources the shared monotonic clock" grep -q 'monotonic_clock.sh' "$ROOT/$f"
	assert "$f has no wall-clock SECONDS deadline" \
		not_grep_re '\(\( *SECONDS *[<>=+-]' "$ROOT/$f"
done

# Every sleep inside one_shot_join.sh's bounded poll is charged against the
# same deadline the loop tests. A fixed re-check window carried a cycle (and
# its timeout verdict) past TIMEOUT_SEC by the length of that window.
assert "one_shot_join.sh clamps its re-check sleep to the remaining budget" \
	grep -q 'recheck_sec=$(( deadline - $(mono_sec) ))' "$ROOT/scripts/one_shot_join.sh"

assert "mono_sec prints an integer" mono_output_is_integer
assert "successive mono_sec reads never go backwards" mono_reads_never_go_backwards
assert "mono_sec advances as time passes" mono_advances_with_time

finish
