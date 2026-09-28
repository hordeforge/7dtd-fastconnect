#!/usr/bin/env bash
# Structural + behavioral tests for unmute_client_audio.sh (no live game).
# The jq stream-matching filter is the same one mute_client_audio.sh uses;
# this exercises unmute against a stub pactl.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

assert "unmute helper executable" test -x "$ROOT/scripts/unmute_client_audio.sh"
assert "README documents unmute command" \
	grep -qF './scripts/unmute_client_audio.sh' "$ROOT/README.md"
assert "mute helper still exists (pair)" test -x "$ROOT/scripts/mute_client_audio.sh"

BEHAV=""
if command -v jq >/dev/null 2>&1; then
	BEHAV="$(scratch_mktemp "$ROOT" unmute-helper)"
	install_pactl_stub "$BEHAV"

	write_audio_streams "$BEHAV/streams.json"
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

	# The "still muted" branch prints a command the user is invited to paste,
	# with the resolved XDG_STATE_HOME path in it. A path carrying quotes,
	# spaces, or shell metacharacters must survive as one inert argument.
	HOSTILE="$BEHAV/wei'rd \$(touch pwned) dir"
	mkdir -p "$HOSTILE/wireplumber"
	printf '%s\n' 'Output/Audio:application.name:7DaysToDie:x={"mute":true}' \
		> "$HOSTILE/wireplumber/stream-properties"
	hint_rc=0
	hint="$(PATH="$BEHAV:$PATH" PACTL_JSON="$BEHAV/streams.json" PACTL_LOG="$BEHAV/unmute.log" \
		XDG_STATE_HOME="$HOSTILE" \
		"$ROOT/scripts/unmute_client_audio.sh" 2>&1 >/dev/null)" || hint_rc=$?
	printf '%s\n' "$hint" > "$BEHAV/hint.txt"
	assert "still-muted branch exits 1" test "$hint_rc" -eq 1
	# The path reaches the hint as a single-quoted argument with the embedded
	# quote escaped, so a paste cannot turn it into shell syntax.
	assert "hint single-quotes the state file path" \
		grep -qF "rd \$(touch pwned) dir/wireplumber/stream-properties'" "$BEHAV/hint.txt"
	assert "hint escapes the embedded quote" grep -qF "'\''" "$BEHAV/hint.txt"
	# The quoted form the user pastes must re-parse to the same one path, with
	# the command substitution still inert.
	hint_line="$(grep -F 'stream-properties' "$BEHAV/hint.txt" | head -1)"
	quoted="${hint_line##*\" }"
	pasted="$(eval "printf '%s' $quoted" 2>/dev/null)"
	assert "pasted path re-parses to exactly the original" \
		test "$pasted" = "$HOSTILE/wireplumber/stream-properties"
	assert "pasting the hint ran no command substitution" test ! -e pwned
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
