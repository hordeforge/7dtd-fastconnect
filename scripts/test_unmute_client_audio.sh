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
FAILBIN=""
NO_PULSE_BIN=""
# One cleanup list for the whole run, with each fixture dir appended to it as
# it is created. Per-fixture traps cannot do that: the last one installed wins,
# so a trap added for a later fixture silently dropped the earlier dirs from
# the cleanup and every run leaked a scratch tree.
trap 'rm -rf ${BEHAV:+"$BEHAV"} ${FAILBIN:+"$FAILBIN"} ${NO_PULSE_BIN:+"$NO_PULSE_BIN"}' EXIT
if command -v jq >/dev/null 2>&1; then
	BEHAV="$(scratch_mktemp "$ROOT" unmute-helper)"
	install_pactl_stub "$BEHAV"

	write_audio_streams "$BEHAV/streams.json"
	: > "$BEHAV/unmute.log"
	if PATH="$BEHAV:$PATH" PACTL_JSON="$BEHAV/streams.json" PACTL_LOG="$BEHAV/unmute.log" \
		"$ROOT/scripts/unmute_client_audio.sh" >"$BEHAV/out" 2>"$BEHAV/err"; then
		assert "unmutes stream matched by application name" grep -qx 'set-sink-input-mute 7 0' "$BEHAV/unmute.log"
		assert "unmutes stream matched case-insensitively by binary" grep -qx 'set-sink-input-mute 11 0' "$BEHAV/unmute.log"
		assert "leaves unrelated streams alone" not_grep_re '^set-sink-input-mute 9 ' "$BEHAV/unmute.log"
		# The two positives above pass on a filter that also unmutes something
		# else, so the count pins the filter to exactly the matching streams.
		assert "unmutes nothing beyond the two matches" \
			test "$(wc -l <"$BEHAV/unmute.log")" -eq 2
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

	# pactl refusing the set-sink-input-mute leaves the stream muted, so the
	# helper must not report success: exiting 0 would send the user away
	# believing the next launch has sound.
	FAILBIN="$(scratch_mktemp "$ROOT" unmute-pactl-fail)"
	install_pactl_stub "$FAILBIN"
	# The previous case left the fixture empty; a refusal is only visible when
	# there is a live stream to refuse.
	write_audio_streams "$BEHAV/streams.json"
	cat >"$FAILBIN/pactl" <<-'STUB'
		case "$1" in
		-f) cat "${PACTL_JSON:?}" ;;
		set-sink-input-mute) echo 'pactl: stream not found' >&2; exit 1 ;;
		*) echo "unexpected pactl call: $*" >&2; exit 1 ;;
		esac
	STUB
	chmod +x "$FAILBIN/pactl"
	fail_rc=0
	PATH="$FAILBIN:$PATH" PACTL_JSON="$BEHAV/streams.json" PACTL_LOG="$BEHAV/unmute.log" \
		XDG_STATE_HOME="$BEHAV" \
		"$ROOT/scripts/unmute_client_audio.sh" >"$BEHAV/out" 2>"$BEHAV/err" || fail_rc=$?
	assert "refused unmute exits 1" test "$fail_rc" -eq 1
	assert "refused unmute names the stream" grep -q 'could not unmute sink input 7' "$BEHAV/err"
	assert "refused unmute says the client may start silent" \
		grep -q 'the client may start silent' "$BEHAV/err"
	assert "refused unmute claims no success" not_grep 'Unmuted 7 Days To Die' "$BEHAV/out"

	# The refusal above fails every index, so it cannot tell a helper that
	# gives up on the first refusal from one that keeps going. Here only
	# stream 7 is refused: the helper must still un mute 11 and still report
	# the failure, because a helper that stopped at the first error would
	# leave 11 muted and WirePlumber would persist that for the next launch.
	cat >"$FAILBIN/pactl" <<-'STUB'
		case "$1" in
		-f) cat "${PACTL_JSON:?}" ;;
		set-sink-input-mute)
			[ "$2" = 7 ] && { echo 'pactl: stream not found' >&2; exit 1; }
			printf '%s\n' "$*" >>"${PACTL_LOG:?}"
			;;
		*) echo "unexpected pactl call: $*" >&2; exit 1 ;;
		esac
	STUB
	chmod +x "$FAILBIN/pactl"
	: > "$BEHAV/unmute.log"
	partial_rc=0
	PATH="$FAILBIN:$PATH" PACTL_JSON="$BEHAV/streams.json" PACTL_LOG="$BEHAV/unmute.log" \
		XDG_STATE_HOME="$BEHAV" \
		"$ROOT/scripts/unmute_client_audio.sh" >"$BEHAV/out" 2>"$BEHAV/err" || partial_rc=$?
	assert "a partly refused unmute still exits 1" test "$partial_rc" -eq 1
	assert "a partly refused unmute keeps going past the refusal" \
		grep -qx 'set-sink-input-mute 11 0' "$BEHAV/unmute.log"
	assert "the refused stream is still named" \
		grep -q 'could not unmute sink input 7' "$BEHAV/err"
	assert "only the stream that changed is reported" \
		grep -q 'Unmuted 7 Days To Die audio stream (sink input 11)' "$BEHAV/out" \
		&& not_grep 'sink input 7' "$BEHAV/out"
	# Back to no live stream for the branches below.
	printf '%s\n' '[]' > "$BEHAV/streams.json"

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
	# The paste runs in its own empty cwd: a regression that let the command
	# substitution through would drop a pwned file in whatever directory the
	# gate was started from (the repo root), and the assertion would then read
	# a stray file from an earlier run instead of this one.
	PASTE_DIR="$BEHAV/paste"
	mkdir -p "$PASTE_DIR"
	pasted="$(cd "$PASTE_DIR" && eval "printf '%s' $quoted" 2>/dev/null)"
	assert "pasted path re-parses to exactly the original" \
		test "$pasted" = "$HOSTILE/wireplumber/stream-properties"
	assert "pasting the hint ran no command substitution" \
		test -z "$(ls -A "$PASTE_DIR")"
else
	echo "SKIP behavioral unmute checks (jq missing)" >&2
fi

NO_PULSE_BIN="$(scratch_mktemp "$ROOT" unmute-nopulse)"
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
