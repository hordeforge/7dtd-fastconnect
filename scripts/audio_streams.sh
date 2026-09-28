#!/usr/bin/env bash
# Shared PipeWire/Pulse stream matching for the mute/unmute pair. Both
# helpers must agree on which sink inputs belong to the game, or unmute would
# look at different streams than mute touched while WirePlumber persists the
# saved state under those same names. Source this file; do not execute it.

# Seconds a single pactl call may take. pactl talks to the Pulse/PipeWire
# server over its socket and sets no timeout of its own, so a wedged audio
# server leaves the call blocked indefinitely: the mute poller then never
# reaches its next poll and the unmute helper never reports, both of which
# read as "no stream" only long after the window is gone. Dropped to a plain
# pactl call when coreutils' timeout is not installed.
AUDIO_PACTL_TIMEOUT_SEC=10

pactl_bounded() {
	if command -v timeout >/dev/null 2>&1; then
		timeout "$AUDIO_PACTL_TIMEOUT_SEC" pactl "$@"
	else
		pactl "$@"
	fi
}

# Echo the sink-input indexes whose application name or process binary names
# the game (case-insensitive), one per line; empty when none match.
game_sink_indexes() {
	pactl_bounded -f json list sink-inputs 2>/dev/null | jq -r '
		.[]
		| select(
			((.properties["application.name"] // "")
				+ " "
				+ (.properties["application.process.binary"] // ""))
			| test("7DaysToDie"; "i")
		)
		| .index
	' 2>/dev/null || true
}

# Apply set-sink-input-mute STATE (1=mute, 0=unmute) to every index piped on
# stdin. VERB is the action ("mute" / "unmute"), used for both the report
# line and the failure warning.
# Returns 1 when any index failed, so a caller that has to report success to
# someone (unmute) does not claim a stream it could not change; the mute
# helper ignores the status on purpose, because audio must never fail a
# launch.
apply_game_stream_mute() {
	local state="$1" verb="$2" index failed=0 past
	case "$verb" in
	mute) past=Muted ;;
	unmute) past=Unmuted ;;
	*)
		echo "WARN: apply_game_stream_mute: unknown action '$verb'" >&2
		return 1
		;;
	esac
	while read -r index; do
		[[ -z "$index" ]] && continue
		# The stream can vanish between listing and muting, and a wedged
		# audio server can time the call out; one failure must not abort
		# the rest of the list (best-effort helper).
		if pactl_bounded set-sink-input-mute "$index" "$state" 2>/dev/null; then
			echo "$past 7 Days To Die audio stream (sink input $index)."
		else
			failed=1
			echo "WARN: pactl could not ${verb} sink input $index (stream closed, or pactl did not answer within ${AUDIO_PACTL_TIMEOUT_SEC}s)." >&2
		fi
	done
	return "$failed"
}
