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
# Misses also do not rescan from byte zero: each pattern resumes from its own
# offset in MARK_OFFSET, so a poll greps only the bytes appended since that
# pattern last scanned plus an overlap window. Per-pattern watermarks matter
# because callers query patterns conditionally (one_shot_join.sh scans some
# markers only after another marker matched): a single offset shared by all
# patterns is advanced by whichever patterns happen to run, so between two
# scans of a conditionally-queried pattern it could skip bytes that were
# never examined for it. The overlap keeps a match that straddles a scan
# boundary visible: a line still being written when one scan reached the end
# is inside the next scan's window instead of being split away. A match would
# have to span more than LOG_MARK_OVERLAP of a single line to be missed,
# which does not happen in game logs; the window also bounds the worst case
# when polls arrive faster than bytes are appended. External truncation (size
# shrinking below the offset) falls back to a full scan.

declare -A SEEN_MARK=()

# Bytes re-examined before the resume point on every scan.
LOG_MARK_OVERLAP=262144

# Byte offset up to which each queried pattern has been scanned.
declare -A MARK_OFFSET=()

# Drops all memoized positives and the resume offsets; required whenever
# LOG_MARK_FILE is truncated or replaced so a stale match or a stale offset
# cannot leak into a new cycle.
log_marks_reset() {
	SEEN_MARK=()
	MARK_OFFSET=()
}

# Returns 0 when the ERE has ever matched LOG_MARK_FILE, 1 otherwise.
log_seen() {
	local re="$1"
	if [[ -n "${SEEN_MARK[$re]+x}" ]]; then
		return "${SEEN_MARK[$re]}"
	fi
	local size
	size="$(stat -c %s "$LOG_MARK_FILE" 2>/dev/null)" || return 1
	local off="${MARK_OFFSET[$re]:-0}"
	if ((size < off)); then
		off=0
	fi
	local start=$((off - LOG_MARK_OVERLAP))
	if ((start < 0)); then start=0; fi
	# tail -c +N is 1-based; a start past EOF yields an empty stream, which
	# correctly matches nothing.
	if grep -Eq -- "$re" <(tail -c +"$((start + 1))" "$LOG_MARK_FILE" 2>/dev/null); then
		SEEN_MARK[$re]=0
		MARK_OFFSET[$re]=$size
		return 0
	fi
	MARK_OFFSET[$re]=$size
	return 1
}
