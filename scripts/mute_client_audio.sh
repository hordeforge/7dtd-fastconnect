#!/usr/bin/env bash
# Mute the 7 Days To Die PipeWire/PulseAudio sink-input (opt-out for tests).
# Used by launch_client.sh (default on). Does not touch master volume or
# in-game sliders. WirePlumber may persist mute by application.name — run
# with the game up and unmute via the desktop mixer, or:
#   pactl set-sink-input-mute <index> 0
#
# Env:
#   CLIENT_MUTE_TIMEOUT  poll seconds for the stream (default 60)
set -euo pipefail

WAIT_SECONDS="${1:-${CLIENT_MUTE_TIMEOUT:-${SEVEN_DAYS_TO_DIE_CLIENT_MUTE_TIMEOUT:-60}}}"

if ! [[ "$WAIT_SECONDS" =~ ^[0-9]+$ ]] || ((WAIT_SECONDS < 1)); then
	echo "WARN: CLIENT_MUTE_TIMEOUT invalid; using 60." >&2
	WAIT_SECONDS=60
fi

if ! command -v pactl >/dev/null 2>&1 || ! command -v jq >/dev/null 2>&1; then
	echo "WARN: pactl and jq required to mute client; leaving audio unmuted." >&2
	exit 0
fi

deadline=$((SECONDS + WAIT_SECONDS))
while ((SECONDS < deadline)); do
	indexes="$(pactl -f json list sink-inputs 2>/dev/null | jq -r '
		.[]
		| select(
			((.properties["application.name"] // "")
				+ " "
				+ (.properties["application.process.binary"] // ""))
			| test("7DaysToDie"; "i")
		)
		| .index
	' 2>/dev/null || true)"
	if [[ -n "$indexes" ]]; then
		while read -r index; do
			[[ -z "$index" ]] && continue
			pactl set-sink-input-mute "$index" 1
			echo "Muted 7 Days To Die audio stream (sink input $index)."
		done <<< "$indexes"
		exit 0
	fi
	sleep 1
done

echo "WARN: no 7 Days To Die audio stream within ${WAIT_SECONDS}s; not muted." >&2
exit 0
