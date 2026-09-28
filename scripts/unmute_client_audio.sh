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

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  cat <<'EOF'
Usage: unmute_client_audio.sh

Unmute the 7 Days To Die audio stream (pair of mute_client_audio.sh).
Run it while the client is up so WirePlumber persists the unmuted state;
with the game closed it reports whether the saved state would still start
the next launch muted.

Exit status: 0 stream unmuted or nothing to do | 1 pactl/jq missing, pactl
refused a stream, or no live stream while the saved state is still muted
| 2 usage error.
EOF
  exit 0
fi

if (( $# != 0 )); then
	echo "usage: ${0##*/} (takes no arguments; got $#)" >&2
	exit 2
fi

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
	# A stream pactl refused to unmute is still muted: reporting success here
	# would send the user away believing the client has sound.
	if ! apply_game_stream_mute 0 unmute <<<"$indexes"; then
		echo "One or more streams could not be unmuted; the client may start silent." >&2
		exit 1
	fi
	exit 0
fi

echo "No running 7 Days To Die audio stream found." >&2

if grep -qiE '^Output/Audio:application\.name:7DaysToDie[^=]*=.*"mute":true' "$STATE_FILE" 2>/dev/null; then
	# The resolved path is shown, since XDG_STATE_HOME may move it away from
	# ~/.local/state and a hint pointing at a file the user does not have is
	# worse than none. It is single-quoted with embedded quotes and control
	# characters escaped: this is a command the user is invited to paste, so a
	# path carrying a space must survive the shell and a path carrying shell
	# metacharacters must not become part of the command.
	state_arg="$(printf '%s' "$STATE_FILE" | sed "s/'/'\\\\''/g" | tr '\000-\037\177' ' ')"
	cat >&2 <<-EOF

		The saved state still says muted, so the next launch will start silent.
		Start the game and run this script again, or edit the saved state
		directly and restart WirePlumber so it reloads the file:

		    "\${EDITOR:-nano}" '$state_arg'
		    systemctl --user restart wireplumber
	EOF
	exit 1
fi

echo "Saved state is not muted; nothing to do." >&2
