#!/usr/bin/env bash
# Behavioral tests for scripts/log_markers.sh, the append-only marker cache
# used by the join poll in one_shot_join.sh:
#   - positives are cached (a matched marker can never un-match an
#     append-only log), so later polls skip rescanning the file
#   - misses are never cached: bytes appended after a miss must flip it
#   - a missing log file counts as "not seen"
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/7dtd-log-markers.XXXXXX")"
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

assert "missing log is not seen" missing_log_is_not_seen
assert "miss flips when matching bytes arrive" miss_flips_when_bytes_arrive
assert "cached positive survives other-marker misses" positive_sticks_after_miss_on_other_marker
assert "distinct patterns do not collide" distinct_patterns_do_not_collide
assert "partial line matches only once complete" partial_line_does_not_match_until_complete

finish
