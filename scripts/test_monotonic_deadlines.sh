#!/usr/bin/env bash
# Bounded waits must measure elapsed time on a monotonic clock (/proc/uptime,
# CLOCK_BOOTTIME), not Bash's wall-clock-derived $SECONDS: an NTP step or
# manual correction mid-wait extends or truncates timeouts, so a join cycle
# would hang past its budget or kill a client that was about to spawn.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

not_grep_re() {
	local pattern="$1" file="$2"
	! grep -Eq -- "$pattern" "$file"
}

for f in scripts/mute_client_audio.sh scripts/one_shot_join.sh; do
	assert "$f reads monotonic uptime" grep -q '/proc/uptime' "$ROOT/$f"
	assert "$f has no wall-clock SECONDS deadline" \
		not_grep_re '\(\( *SECONDS *[<>=+-]' "$ROOT/$f"
done

finish
