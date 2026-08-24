#!/usr/bin/env bash
# Memoized marker matches against an append-only log for join harnesses.
#
# Contract: the caller points LOG_MARK_FILE at a log that is truncated once
# and only appended to afterwards (the game writes it during the cycle). A
# matched regex can therefore never un-match, so positives are cached and
# later polls skip rescanning a log that grows by megabytes. Unmatched
# patterns are never cached: fresh bytes must be able to flip them, so every
# miss rescans only the still-unmatched markers.

declare -A SEEN_MARK=()

# Drops all memoized positives; required whenever LOG_MARK_FILE is truncated
# or replaced so a stale match cannot leak into a new cycle.
log_marks_reset() {
	SEEN_MARK=()
}

# Returns 0 when the ERE has ever matched LOG_MARK_FILE, 1 otherwise.
log_seen() {
	local re="$1"
	if [[ -n "${SEEN_MARK[$re]+x}" ]]; then
		return "${SEEN_MARK[$re]}"
	fi
	if grep -Eq -- "$re" "$LOG_MARK_FILE" 2>/dev/null; then
		SEEN_MARK[$re]=0
		return 0
	fi
	return 1
}
