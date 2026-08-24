#!/usr/bin/env bash
# Test double for pactl, shared by the mute/unmute gates and the launcher
# platform tests: -f serves the canned stream list from PACTL_JSON and every
# set-sink-input-mute call is recorded into PACTL_LOG, so the audio helpers
# are exercised without an audio server. Any other invocation fails loudly.
case "$1" in
	-f) cat "${PACTL_JSON:?}" ;;
	set-sink-input-mute) printf '%s\n' "$*" >>"${PACTL_LOG:?}" ;;
	*) echo "unexpected pactl call: $*" >&2; exit 1 ;;
esac
