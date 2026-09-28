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
# shrinking below the largest size ever seen) drops every memoized verdict and
# falls back to a full scan.
#
# A poll that finds the file exactly the size this pattern last scanned to
# skips the scan outright: the window is byte-identical, so the verdict cannot
# differ, and re-grepping it every 2s is the bulk of a join cycle's cost. The
# join log is bursty (minutes of setup around a few seconds of world load), so
# most polls land on a log that has not grown. This assumes append-only as
# above: a log shorter than the longest one seen is a truncate or replace and
# drops every memoized verdict, but content replaced by a file of exactly the
# same size stays unnoticed.

declare -A SEEN_MARK=()

# Bytes re-examined before the resume point on every scan.
LOG_MARK_OVERLAP=262144

# Byte offset up to which each queried pattern has been scanned.
declare -A MARK_OFFSET=()

# Largest size LOG_MARK_FILE has ever been seen at. A poll below it means the
# log shrank, which append-only writing never does, so the file was truncated
# or replaced and every memoized verdict refers to bytes that are gone.
MARK_MAX_SIZE=0

# Scan errors already reported, so a poll that re-queries the same failing
# pattern every 2s cannot bury the log it is reporting on.
declare -A MARK_ERROR_SEEN=()

# A scan that could not be run at all (unreadable log, grep error) is not a
# miss: the pattern has to answer "not seen" until it recovers, and the
# operator needs to know the verdict is missing rather than negative.
log_mark_error() {
	local msg="$1"
	if [[ -n "${MARK_ERROR_SEEN[$msg]+x}" ]]; then
		return 0
	fi
	MARK_ERROR_SEEN[$msg]=1
	echo "WARN: log marker scan failed ($msg) on $LOG_MARK_FILE; matching patterns answer 'not seen' until the scan recovers" >&2
}

# Drops all memoized positives and the resume offsets; required whenever
# LOG_MARK_FILE is truncated or replaced so a stale match or a stale offset
# cannot leak into a new cycle. A shrink below MARK_MAX_SIZE does the same on
# its own, so a truncation between two polls cannot leave a positive behind.
log_marks_reset() {
	SEEN_MARK=()
	MARK_OFFSET=()
	MARK_ERROR_SEEN=()
	MARK_MAX_SIZE=0
}

# Returns 0 when the ERE has ever matched LOG_MARK_FILE, 1 otherwise.
log_seen() {
	local re="$1"
	if [[ -n "${SEEN_MARK[$re]+x}" ]]; then
		return "${SEEN_MARK[$re]}"
	fi
	local size
	# Byte count via `wc -c` on a redirected stdin, the one size probe that
	# behaves the same on every host. GNU `stat -c` is not understood by the
	# BSD/macOS stat, and its failure here was silent rather than loud: the
	# substitution failed, log_seen answered "not seen" for every pattern, and
	# the join poll in one_shot_join.sh reported a timeout for a cycle that had
	# actually joined. The existence test comes first: a redirected wc on a log
	# the game has not created yet fails in the shell, and the discarded stderr
	# on the command cannot silence that. Strip the padding some wc builds print
	# before the count.
	[[ -f "$LOG_MARK_FILE" ]] || return 1
	size="$(wc -c <"$LOG_MARK_FILE" 2>/dev/null)" || return 1
	size="${size//[[:space:]]/}"
	[[ "$size" =~ ^[0-9]+$ ]] || return 1
	# Shorter than the log has ever been: truncated or replaced, so the
	# positive another pattern memoized came from bytes that no longer exist
	# and must not be reported as a match in the new file. Checked before the
	# idle fast path below, because a replacement of the same length looks
	# like an un-grown log. Offsets go too: every one of them points into the
	# old file, so keeping them would skip the replacement's opening bytes.
	if ((size < MARK_MAX_SIZE)); then
		log_marks_reset
	fi
	if ((size > MARK_MAX_SIZE)); then
		MARK_MAX_SIZE=$size
	fi
	local off="${MARK_OFFSET[$re]:-0}"
	# No bytes appended since this pattern's last scan: the window that scan
	# covered is byte-identical, so grep would return the same verdict. The
	# poll re-queries every still-unmatched marker every 2s for the whole join
	# budget, and a log that sits still between bursts is the common case, so
	# this drops the tail+grep forks (and the overlap re-read) for those polls.
	# A positive already returned above; a miss is the only verdict left.
	if ((size == off)); then
		return 1
	fi
	if ((size < off)); then
		off=0
	fi
	local start=$((off - LOG_MARK_OVERLAP))
	if ((start < 0)); then start=0; fi
	local grep_status
	# A resume at byte zero is the common cold case (first poll of every
	# pattern, or post-truncation fallback): grep the file directly instead
	# of forking tail to copy the whole log through a pipe first.
	if ((start == 0)); then
		grep -Eq -- "$re" "$LOG_MARK_FILE" 2>/dev/null
		grep_status=$?
	else
		# tail -c +N is 1-based; a start past EOF yields an empty stream, which
		# correctly matches nothing.
		# Process substitution, not a pipeline: grep -Eq exits on the first match,
		# which SIGPIPEs a piped tail, and under the caller's `set -o pipefail`
		# that 141 becomes the pipeline status and turns a match into a miss.
		grep -Eq -- "$re" <(tail -c +"$((start + 1))" "$LOG_MARK_FILE" 2>/dev/null)
		grep_status=$?
	fi
	# Status 2 is an error, not a miss. The offset must not advance past bytes
	# that were never scanned, or the pattern reports "not seen" for the rest
	# of the cycle: the same silent-probe-failure class the size probe above
	# was fixed for.
	case "$grep_status" in
		0) ;;
		1) ;;
		*)
			log_mark_error "$re (grep status $grep_status)"
			return 1
			;;
	esac
	local found=1
	if ((grep_status == 0)); then
		found=0
	fi
	MARK_OFFSET[$re]=$size
	if ((found)); then
		return 1
	fi
	SEEN_MARK[$re]=0
	return 0
}
