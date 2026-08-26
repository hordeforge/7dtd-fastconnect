#!/usr/bin/env bash
# Behavioral tests for scripts/log_markers.sh, the append-only marker cache
# used by the join poll in one_shot_join.sh:
#   - positives are cached (a matched marker can never un-match an
#     append-only log), so later polls skip rescanning the file
#   - misses are never cached: bytes appended after a miss must flip it
#   - a missing log file counts as "not seen"
#   - scans resume from an offset (only new bytes plus the overlap window),
#     so a match split across a poll boundary is still found
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

WORK="$(scratch_mktemp "$ROOT" 7dtd-log-markers)"
trap 'rm -rf "$WORK"' EXIT

# shellcheck disable=SC2034  # consumed by log_markers.sh via LOG_MARK_FILE
LOG_MARK_FILE="$WORK/client.log"
source "$ROOT/scripts/log_markers.sh"

missing_log_is_not_seen() {
	log_marks_reset
	[[ ! -e "$LOG_MARK_FILE" ]] && ! log_seen 'Joined world'
}

miss_flips_when_bytes_arrive() {
	log_marks_reset
	: >"$LOG_MARK_FILE"
	if log_seen 'Found own player entity with id'; then return 1; fi
	printf 'NET: Found own player entity with id 42\n' >>"$LOG_MARK_FILE"
	log_seen 'Found own player entity with id'
}

positive_sticks_after_miss_on_other_marker() {
	log_marks_reset
	log_seen 'Found own player entity with id' || return 1
	# A miss for a second marker must not evict or shadow the first positive.
	log_seen 'Kicked from server' && return 1
	log_seen 'Found own player entity with id'
}

distinct_patterns_do_not_collide() {
	log_marks_reset
	printf 'NET: LiteNetLib: Accepted by server\n' >>"$LOG_MARK_FILE"
	log_seen 'Accepted by server' || return 1
	log_seen 'PlayerSpawnedInWorld' && return 1
	return 0
}

partial_line_does_not_match_until_complete() {
	log_marks_reset
	: >"$LOG_MARK_FILE"
	printf 'NET: Found own player enti' >"$LOG_MARK_FILE"
	if log_seen 'Found own player entity with id'; then return 1; fi
	printf 'ty with id 7\n' >>"$LOG_MARK_FILE"
	log_seen 'Found own player entity with id'
}

# The offset resume: after scanning a file larger than the overlap window,
# the pattern's MARK_OFFSET entry must sit at EOF, and a marker completed
# across the next poll boundary (its first half already inside the overlap
# window, not before it) must still be found.
offset_advances_and_boundary_match_is_found() {
	local old_overlap="$LOG_MARK_OVERLAP"
	LOG_MARK_OVERLAP=1024
	log_marks_reset
	: >"$LOG_MARK_FILE"
	head -c 4096 /dev/zero | tr '\0' 'x' >>"$LOG_MARK_FILE"
	printf 'NET: Found own player enti' >>"$LOG_MARK_FILE"
	if log_seen 'Found own player entity with id'; then
		LOG_MARK_OVERLAP="$old_overlap"
		return 1
	fi
	local off="${MARK_OFFSET['Found own player entity with id']}"
	((off == $(wc -c <"$LOG_MARK_FILE"))) || {
		LOG_MARK_OVERLAP="$old_overlap"
		return 1
	}
	printf 'ty with id 9\n' >>"$LOG_MARK_FILE"
	local rc=0
	if log_seen 'Found own player entity with id'; then rc=0; else rc=1; fi
	LOG_MARK_OVERLAP="$old_overlap"
	return "$rc"
}

# External truncation (file shrinks below the recorded offset) falls back to
# a full scan instead of skipping everything.
truncated_log_resets_offset() {
	log_marks_reset
	: >"$LOG_MARK_FILE"
	head -c 4096 /dev/zero | tr '\0' 'x' >>"$LOG_MARK_FILE"
	log_seen 'never written anywhere' || true
	((MARK_OFFSET['never written anywhere'] > 0)) || return 1
	: >"$LOG_MARK_FILE"
	printf 'NET: PlayerSpawnedInWorld\n' >>"$LOG_MARK_FILE"
	log_seen 'PlayerSpawnedInWorld'
}

# A pattern queried conditionally (only after another marker matched) must not
# skip bytes just because other patterns' scans advanced past them in between:
# each pattern resumes from its own offset, so growth carrying a match between
# two scans of the same pattern is always covered.
conditionally_queried_pattern_misses_nothing() {
	local old_overlap="$LOG_MARK_OVERLAP"
	LOG_MARK_OVERLAP=1024
	log_marks_reset
	: >"$LOG_MARK_FILE"
	head -c 4096 /dev/zero | tr '\0' 'x' >>"$LOG_MARK_FILE"
	log_seen 'alpha never written' || true
	log_seen 'NET: beta marker' || true
	# Growth larger than the overlap window carries beta's match; an unrelated
	# pattern is scanned afterwards (advancing its own resume point) before
	# beta is re-queried.
	head -c 2048 /dev/zero | tr '\0' 'x' >>"$LOG_MARK_FILE"
	printf 'NET: beta marker\n' >>"$LOG_MARK_FILE"
	head -c 2000 /dev/zero | tr '\0' 'x' >>"$LOG_MARK_FILE"
	log_seen 'alpha never written' || true
	local rc=0
	log_seen 'NET: beta marker' || rc=1
	LOG_MARK_OVERLAP="$old_overlap"
	return "$rc"
}

assert "missing log is not seen" missing_log_is_not_seen
assert "miss flips when matching bytes arrive" miss_flips_when_bytes_arrive
assert "cached positive survives other-marker misses" positive_sticks_after_miss_on_other_marker
assert "distinct patterns do not collide" distinct_patterns_do_not_collide
assert "partial line matches only once complete" partial_line_does_not_match_until_complete
assert "scan offset advances and boundary match is found" offset_advances_and_boundary_match_is_found
assert "truncated log falls back to full scan" truncated_log_resets_offset
assert "conditionally queried pattern skips no bytes" conditionally_queried_pattern_misses_nothing

finish
