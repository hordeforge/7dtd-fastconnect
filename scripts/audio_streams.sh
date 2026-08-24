#!/usr/bin/env bash
# Shared PipeWire/Pulse stream matching for the mute/unmute pair. Both
# helpers must agree on which sink inputs belong to the game, or unmute would
# look at different streams than mute touched while WirePlumber persists the
# saved state under those same names. Source this file; do not execute it.

# Echo the sink-input indexes whose application name or process binary names
# the game (case-insensitive), one per line; empty when none match.
game_sink_indexes() {
	pactl -f json list sink-inputs 2>/dev/null | jq -r '
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
# stdin. VERB ("Muted" / "Unmuted") names the action in the report lines.
apply_game_stream_mute() {
	local state="$1" verb="$2" index
	while read -r index; do
		[[ -z "$index" ]] && continue
		# The stream can vanish between listing and muting; one failure
		# must not abort the rest of the list (best-effort helper).
		if pactl set-sink-input-mute "$index" "$state" 2>/dev/null; then
			echo "${verb} 7 Days To Die audio stream (sink input $index)."
		else
			echo "WARN: could not ${verb,,} sink input $index (stream may have closed)." >&2
		fi
	done
}
