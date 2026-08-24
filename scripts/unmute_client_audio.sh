#!/usr/bin/env bash
# Unmute the 7 Days To Die PipeWire/PulseAudio sink-input after a muted test
# launch. Pair of mute_client_audio.sh.
#
# OS-level only: pactl set-sink-input-mute 0 on the live stream. Never touches
# game client settings. WirePlumber persists per-app stream mute by
# application.name, so this must run while the game is up for the saved state
# to flip back.
#
# Usage:
#   ./scripts/unmute_client_audio.sh
set -euo pipefail

STATE_FILE="${XDG_STATE_HOME:-$HOME/.local/state}/wireplumber/stream-properties"

if ! command -v pactl >/dev/null 2>&1 || ! command -v jq >/dev/null 2>&1; then
	echo "ERROR: pactl and jq are required." >&2
	exit 1
fi

# Stream matching shared with mute_client_audio.sh (same rule, or unmute
# would look at different streams than mute touched).
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/audio_streams.sh"

indexes="$(game_sink_indexes)"

if [[ -n "$indexes" ]]; then
	apply_game_stream_mute 0 Unmuted <<<"$indexes"
	exit 0
fi

echo "No running 7 Days To Die audio stream found." >&2

if grep -qiE '^Output/Audio:application\.name:7DaysToDie[^=]*=.*"mute":true' "$STATE_FILE" 2>/dev/null; then
	cat >&2 <<-'EOF'

		The saved state still says muted, so the next launch will start silent.
		Start the game and run this script again, or edit the saved state
		directly and restart WirePlumber so it reloads the file:

		    "${EDITOR:-nano}" ~/.local/state/wireplumber/stream-properties
		    systemctl --user restart wireplumber
	EOF
	exit 1
fi

echo "Saved state is not muted; nothing to do." >&2
