#!/usr/bin/env bash
# Mute the 7 Days To Die PipeWire/PulseAudio sink-input (opt-out for tests).
# Used by launch_client.sh (default on).
#
# OS-level only: pactl set-sink-input-mute on the live stream. Never touches
# game client settings (no GamePrefs, no in-game audio sliders, no registry /
# user.reg audio prefs, no -volume or similar argv).
#
# WirePlumber may persist per-app stream mute by application.name (still OS
# audio, not the game). Unmute while running:
#   ./scripts/unmute_client_audio.sh
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

# Monotonic deadline source shared with one_shot_join.sh: see
# scripts/monotonic_clock.sh for why $SECONDS must not bound this poll.
# Stream matching comes from audio_streams.sh, shared with unmute_client_audio.sh.
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/monotonic_clock.sh"
source "$SCRIPT_DIR/audio_streams.sh"

deadline=$(( $(mono_sec) + WAIT_SECONDS ))
while (( $(mono_sec) < deadline )); do
	indexes="$(game_sink_indexes)"
	if [[ -n "$indexes" ]]; then
		apply_game_stream_mute 1 Muted <<<"$indexes"
		exit 0
	fi
	sleep 1
done

echo "WARN: no 7 Days To Die audio stream within ${WAIT_SECONDS}s; not muted." >&2
exit 0
