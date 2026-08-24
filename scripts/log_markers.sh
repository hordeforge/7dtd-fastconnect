#!/usr/bin/env bash
# Memoized marker matches against an append-only log for join harnesses.
#
# Contract: the caller points LOG_MARK_FILE at a log that is truncated once
# and only appended to afterwards (the game writes it during the cycle). A
# matched regex can therefore never un-match, so positives are cached and
# later polls skip rescanning a log that grows by megabytes. Unmatched
# patterns are never cached: fresh bytes must be able to flip them, so every
# miss rescans only the still-unmatched markers.
#
# Misses also do not rescan from byte zero: LOG_MARK_OFFSET tracks how far
# scans have reached, so a poll greps only the bytes appended since the last
# poll plus an overlap window. The overlap keeps a match that straddles a
# poll boundary visible: a line still being written when one poll scanned to
# the end is inside the next poll's window instead of being split away. A
# match would have to span more than LOG_MARK_OVERLAP of a single line to be
# missed, which does not happen in game logs; the window also bounds the
# worst case when polls arrive faster than bytes are appended. External
# truncation (size shrinking below the offset) falls back to a full scan.

declare -A SEEN_MARK=()

# Bytes re-examined before the resume point on every scan.
LOG_MARK_OVERLAP=262144

# Byte offset up to which every queried pattern has been scanned.
LOG_MARK_OFFSET=0

# Drops all memoized positives and the resume offset; required whenever
# LOG_MARK_FILE is truncated or replaced so a stale match or a stale offset
# cannot leak into a new cycle.
log_marks_reset() {
	SEEN_MARK=()
	LOG_MARK_OFFSET=0
}

# Returns 0 when the ERE has ever matched LOG_MARK_FILE, 1 otherwise.
log_seen() {
	local re="$1"
	if [[ -n "${SEEN_MARK[$re]+x}" ]]; then
		return "${SEEN_MARK[$re]}"
	fi
	local size
	size="$(stat -c %s "$LOG_MARK_FILE" 2>/dev/null)" || return 1
	if ((size < LOG_MARK_OFFSET)); then
		LOG_MARK_OFFSET=0
	fi
	local start=$((LOG_MARK_OFFSET - LOG_MARK_OVERLAP))
	if ((start < 0)); then start=0; fi
	# tail -c +N is 1-based; a start past EOF yields an empty stream, which
	# correctly matches nothing.
	if grep -Eq -- "$re" <(tail -c +"$((start + 1))" "$LOG_MARK_FILE" 2>/dev/null); then
		SEEN_MARK[$re]=0
		LOG_MARK_OFFSET=$size
		return 0
	fi
	LOG_MARK_OFFSET=$size
	return 1
}
