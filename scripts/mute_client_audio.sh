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
#   CLIENT_MUTE_TIMEOUT / SEVEN_DAYS_TO_DIE_CLIENT_MUTE_TIMEOUT
#     poll seconds for the stream (default 60; invalid values warn and use 60).
#     The first positional arg overrides both.
set -euo pipefail

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  cat <<'EOF'
Usage: mute_client_audio.sh [wait-seconds]

Mute the 7 Days To Die audio stream (pactl sink-input) as soon as it
appears, polling up to wait-seconds. The first argument overrides
CLIENT_MUTE_TIMEOUT / SEVEN_DAYS_TO_DIE_CLIENT_MUTE_TIMEOUT (default 60).
wait-seconds is 1..3600; anything else warns and falls back to 60.

Exit status: 0 muted, no stream within the window, or pactl/jq missing
(the launch must not fail over audio); each of those warns on stderr.
2 is a usage error (more than one argument).
OS-level only: game client audio settings are never touched.
EOF
  exit 0
fi

if (( $# > 1 )); then
	echo "usage: ${0##*/} [wait-seconds] (got $# argument(s))" >&2
	exit 2
fi

WAIT_SECONDS="${1:-${CLIENT_MUTE_TIMEOUT:-${SEVEN_DAYS_TO_DIE_CLIENT_MUTE_TIMEOUT:-60}}}"

# No dirname: this helper runs with a PATH that holds neither coreutils nor
# pactl (the launch wiring degrades to leaving the audio alone), so the script
# has to locate itself with builtins only.
SCRIPT_DIR="${0%/*}"
if [[ "$SCRIPT_DIR" == "$0" ]]; then SCRIPT_DIR=.; fi
SCRIPT_DIR="$(cd -- "$SCRIPT_DIR" && pwd)"
# Shared value checks (is_mute_wait): see scripts/config_validate.sh. The
# bound is checked here, not left to the arithmetic: the deadline is mono_sec
# + WAIT_SECONDS, which is signed 64-bit, so a value near intmax wraps it
# negative and the poll finds the deadline already passed.
source "$SCRIPT_DIR/config_validate.sh"
# Flattening for the rejection lines below: the value can come from the
# caller's environment, and a newline or a bidi override in it would forge a
# second log line. See scripts/log_sanitize.sh.
source "$SCRIPT_DIR/log_sanitize.sh"

if ! is_mute_wait "$WAIT_SECONDS"; then
	# Name where the value came from: naming only CLIENT_MUTE_TIMEOUT sent the
	# reader looking at an env var that was never set when the bad value was the
	# positional argument.
	if (( $# == 1 )); then
		echo "WARN: wait-seconds must be a positive integer (got '$(sanitize_log_text "$WAIT_SECONDS")'); using 60." >&2
	else
		echo "WARN: CLIENT_MUTE_TIMEOUT must be a positive integer (got '$(sanitize_log_text "$WAIT_SECONDS")'); using 60." >&2
	fi
	WAIT_SECONDS=60
fi

if ! command -v pactl >/dev/null 2>&1 || ! command -v jq >/dev/null 2>&1; then
	echo "WARN: pactl and jq required to mute client; leaving audio unmuted." >&2
	exit 0
fi

# Monotonic deadline source shared with one_shot_join.sh: see
# scripts/monotonic_clock.sh for why $SECONDS must not bound this poll.
# Stream matching comes from audio_streams.sh, shared with unmute_client_audio.sh.
source "$SCRIPT_DIR/monotonic_clock.sh"
source "$SCRIPT_DIR/audio_streams.sh"

deadline=$(( $(mono_sec) + WAIT_SECONDS ))
while (( $(mono_sec) < deadline )); do
	indexes="$(game_sink_indexes)"
	if [[ -n "$indexes" ]]; then
		# Status ignored: audio never fails a launch, and a stream that
		# could not be muted has already been named on stderr.
		apply_game_stream_mute 1 mute <<<"$indexes" || true
		exit 0
	fi
	sleep 1
done

echo "WARN: no 7 Days To Die audio stream within ${WAIT_SECONDS}s; not muted." >&2
exit 0
