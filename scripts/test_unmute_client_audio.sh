#!/usr/bin/env bash
# Structural + behavioral tests for unmute_client_audio.sh (no live game).
# The jq stream-matching filter is the same one mute_client_audio.sh uses;
# this exercises unmute against a stub pactl.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"
not_grep() {
	local pattern="$1" file="$2"
	! grep -q -- "$pattern" "$file"
}

assert "unmute helper executable" test -x "$ROOT/scripts/unmute_client_audio.sh"
assert "README documents unmute command" \
	grep -qF './scripts/unmute_client_audio.sh' "$ROOT/README.md"
assert "mute helper still exists (pair)" test -x "$ROOT/scripts/mute_client_audio.sh"

BEHAV=""
if command -v jq >/dev/null 2>&1; then
	BEHAV="$(scratch_mktemp "$ROOT" unmute-helper)"
	cp "$ROOT/scripts/testdata/pactl_stub.sh" "$BEHAV/pactl"
	chmod +x "$BEHAV/pactl"

	printf '%s\n' '[
	 {"index": 7, "properties": {"application.name": "7DaysToDie"}},
	 {"index": 9, "properties": {"application.name": "spotify"}},
	 {"index": 11, "properties": {"application.process.binary": "7daystodie.exe"}}
	]' > "$BEHAV/streams.json"
	: > "$BEHAV/unmute.log"
	if PATH="$BEHAV:$PATH" PACTL_JSON="$BEHAV/streams.json" PACTL_LOG="$BEHAV/unmute.log" \
		"$ROOT/scripts/unmute_client_audio.sh" >"$BEHAV/out" 2>"$BEHAV/err"; then
		assert "unmutes stream matched by application name" grep -qx 'set-sink-input-mute 7 0' "$BEHAV/unmute.log"
		assert "unmutes stream matched case-insensitively by binary" grep -qx 'set-sink-input-mute 11 0' "$BEHAV/unmute.log"
		assert "leaves unrelated streams alone" not_grep ' 9 ' "$BEHAV/unmute.log"
		assert "reports the unmuted streams" grep -q 'Unmuted 7 Days To Die audio stream' "$BEHAV/out"
	else
		echo "FAIL unmute helper exits nonzero on a match" >&2
		FAILS=$((FAILS + 1))
	fi

	printf '%s\n' '[]' > "$BEHAV/streams.json"
	: > "$BEHAV/unmute.log"
	mkdir -p "$BEHAV/wireplumber"
	: > "$BEHAV/wireplumber/stream-properties"
	if PATH="$BEHAV:$PATH" PACTL_JSON="$BEHAV/streams.json" PACTL_LOG="$BEHAV/unmute.log" \
		XDG_STATE_HOME="$BEHAV" \
		"$ROOT/scripts/unmute_client_audio.sh" >"$BEHAV/out" 2>"$BEHAV/err"; then
		assert "warns when no stream is running" grep -q 'No running 7 Days To Die audio stream' "$BEHAV/err"
		assert "never unmutes without a match" not_grep . "$BEHAV/unmute.log"
	else
		echo "FAIL unmute helper exits nonzero when no stream and no saved mute" >&2
		FAILS=$((FAILS + 1))
	fi
else
	echo "SKIP behavioral unmute checks (jq missing)" >&2
fi

NO_PULSE_BIN="$(scratch_mktemp "$ROOT" unmute-nopulse)"
trap 'rm -rf ${BEHAV:+"$BEHAV"} "$NO_PULSE_BIN"' EXIT
ln -s "$(command -v bash)" "$NO_PULSE_BIN/bash"

HELPER_RC=0
run_helper_without_pulse() {
	set +e
	out="$(PATH="$NO_PULSE_BIN" "$ROOT/scripts/unmute_client_audio.sh" 2>&1)"
	HELPER_RC=$?
	set -e
}

run_helper_without_pulse
assert "helper exits nonzero without pactl/jq" test "$HELPER_RC" -ne 0
assert "helper errors that pactl and jq are required" grep -q 'pactl and jq are required' <<<"$out"

finish
