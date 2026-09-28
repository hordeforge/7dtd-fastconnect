#!/usr/bin/env bash
# Offline gate for one_shot_join.sh's cleanup of the detached launcher. The
# launcher is started with setsid so it can be stopped as a process group:
# signalling only its pid left the mute poller and the Proton stack it forked
# running after the cycle returned, and with them the launcher's own EXIT trap,
# the one that restores platform.cfg. The signal must also fall back to the
# plain pid for a child that does not lead a group, or a negative group id
# names an unrelated group. The script itself is never executed here: it
# launches and kills real clients and sweeps the wine stack.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

src="$ROOT/scripts/one_shot_join.sh"

defines_helper() {
	grep -q '^signal_owned() {' "$src"
}

cleanup_body() {
	sed -n '/^cleanup() {/,/^}/p' "$src"
}

# Both stop paths go through the helper: the launcher group and the plain
# server child are the two children the EXIT trap owns.
stops_both_via_helper() {
	local body
	body="$(cleanup_body)"
	[[ -n "$body" ]] || return 1
	grep -q 'signal_owned "\$launch_pid" KILL' <<<"$body" &&
		grep -q 'signal_owned "\$server_pid" KILL' <<<"$body"
}

# A negative group id is only safe when the pid leads its own group, so the
# helper has to read the pgid and compare it to the pid before signalling one.
checks_group_leadership() {
	grep -q 'ps -o pgid= -p "\$pid"' "$src" &&
		grep -q '\$pgid" == "\$pid"' "$src"
}

# The KILL step runs after the TERM step, by which time the pid may already
# have been recycled; the helper has to re-check liveness before signalling.
rechecks_liveness() {
	grep -q 'kill -0 "\$pid" 2>/dev/null || return 0' "$src"
}

# The child is this shell's own, so a stopped launcher lingers as a zombie for
# the rest of the run unless the trap reaps it.
reaps_launcher() {
	grep -q 'wait "\$launch_pid"' "$src"
}

# The old pid-only kill would satisfy the checks above just as well, so pin
# that it is gone from the cleanup body.
no_pid_only_launcher_kill() {
	local body
	body="$(cleanup_body)"
	[[ -n "$body" ]] || return 1
	! grep -qE 'kill(-9)? "\$(launch|server)_pid"' <<<"$body"
}

assert "one_shot_join.sh defines the group-aware stop helper" defines_helper
assert "cleanup stops launcher and server through it" stops_both_via_helper
assert "a group id is signalled only by a group leader" checks_group_leadership
assert "the helper re-checks liveness before signalling" rechecks_liveness
assert "cleanup reaps the launcher it stopped" reaps_launcher
assert "cleanup no longer signals the tracked pids directly" no_pid_only_launcher_kill

finish
