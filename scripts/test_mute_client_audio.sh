#!/usr/bin/env bash
# Structural + behavioral tests for default-on client mute (no live game or
# audio server required): launch wiring is grepped, and mute_client_audio.sh
# itself is exercised against a stub pactl so the jq stream-matching filter
# actually runs.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

assert "mute helper executable" test -x "$ROOT/scripts/mute_client_audio.sh"
assert "launch_client references mute helper" grep -q 'mute_client_audio.sh' "$ROOT/scripts/launch_client.sh"
assert "launch defaults mute on" grep -q 'MUTE_CLIENT="\$(env_bool' "$ROOT/scripts/launch_client.sh"
# The shared boolean reader takes the default as its last argument, so
# default-on has to be checked at the end of the call, not on the line that
# starts it. A function, not a pipeline: a bare `assert ... | tail` would make
# the pipe the assert's own pipeline and trip set -e on the assert output.
mute_default_is_one() {
	grep -A3 'MUTE_CLIENT="\$(env_bool' "$ROOT/scripts/launch_client.sh" | tail -1 | grep -qx '  1)"'
}
assert "launch passes 1 as the mute default" mute_default_is_one
assert "README documents the CLIENT_MUTE=0 opt-out command" \
	grep -qF 'CLIENT_MUTE=0 ./scripts/launch_client.sh' "$ROOT/README.md"
assert "launch documents opt-out" grep -q 'CLIENT_MUTE=0' "$ROOT/scripts/launch_client.sh"
if grep -qE 'exec "\$PROTON"' "$ROOT/scripts/launch_client.sh"; then
	echo "FAIL launch does not exec proton (mute needs wait)" >&2
	FAILS=$((FAILS + 1))
else
	echo "PASS launch does not exec proton (mute needs wait)"
fi
assert "launch backgrounds mute poll" grep -q 'start_mute_poll' "$ROOT/scripts/launch_client.sh"

BEHAV=""
# Behavioral: the helper's jq filter must mute streams whose application.name
# or (case-insensitive) process binary matches 7DaysToDie, and nothing else.
if command -v jq >/dev/null 2>&1; then
	BEHAV="$(scratch_mktemp "$ROOT" mute-helper)"
	install_pactl_stub "$BEHAV"

	write_audio_streams "$BEHAV/streams.json"
	: > "$BEHAV/mute.log"
	if PATH="$BEHAV:$PATH" PACTL_JSON="$BEHAV/streams.json" PACTL_LOG="$BEHAV/mute.log" \
		"$ROOT/scripts/mute_client_audio.sh" 5 >"$BEHAV/out" 2>"$BEHAV/err"; then
		assert "mutes stream matched by application name" grep -qx 'set-sink-input-mute 7 1' "$BEHAV/mute.log"
		assert "mutes stream matched case-insensitively by binary" grep -qx 'set-sink-input-mute 11 1' "$BEHAV/mute.log"
		assert "leaves unrelated streams unmuted" not_grep_re '^set-sink-input-mute 9 ' "$BEHAV/mute.log"
		# The two positives above pass on a filter that also mutes something
		# else, so the count pins the filter to exactly the matching streams.
		assert "mutes nothing beyond the two matches" \
			test "$(wc -l <"$BEHAV/mute.log")" -eq 2
		assert "reports the muted streams" grep -q 'Muted 7 Days To Die audio stream' "$BEHAV/out"
	else
		echo "FAIL mute helper exits nonzero on a match" >&2
		FAILS=$((FAILS + 1))
	fi

	# No matching stream: bounded poll, warn on stderr, still exit 0.
	printf '%s\n' '[]' > "$BEHAV/streams.json"
	: > "$BEHAV/mute.log"
	if PATH="$BEHAV:$PATH" PACTL_JSON="$BEHAV/streams.json" PACTL_LOG="$BEHAV/mute.log" \
		"$ROOT/scripts/mute_client_audio.sh" 1 >"$BEHAV/out" 2>"$BEHAV/err"; then
		assert "warns when no stream appears in time" grep -q 'no 7 Days To Die audio stream within 1s' "$BEHAV/err"
		assert "never mutes without a match" not_grep . "$BEHAV/mute.log"
	else
		echo "FAIL mute helper exits nonzero on timeout" >&2
		FAILS=$((FAILS + 1))
	fi
else
	echo "SKIP behavioral mute checks (jq missing)" >&2
fi

# Degradation: with neither pactl nor jq on PATH the helper must leave audio
# alone and exit 0 rather than failing the launch. A bin dir holding only bash
# keeps the helper runnable while hiding both tools from it. BEHAV is only set
# when the jq block above ran, so keep it out of the trap when it is empty.
NO_PULSE_BIN="$(scratch_mktemp "$ROOT" mute-nopulse)"
trap 'rm -rf ${BEHAV:+"$BEHAV"} "$NO_PULSE_BIN"' EXIT
ln -s "$(command -v bash)" "$NO_PULSE_BIN/bash"

HELPER_RC=0
run_helper_without_pulse() {
	set +e
	out="$(PATH="$NO_PULSE_BIN" "$ROOT/scripts/mute_client_audio.sh" "$@" 2>&1)"
	HELPER_RC=$?
	set -e
}

run_helper_without_pulse
assert "helper exits 0 without pactl/jq" test "$HELPER_RC" -eq 0
assert "helper warns it is leaving audio unmuted" grep -q 'leaving audio unmuted' <<<"$out"

run_helper_without_pulse abc
assert "non-numeric timeout still exits 0 without pactl/jq" test "$HELPER_RC" -eq 0
# The warning names the source that carried the bad value, so the reader does
# not go looking at an env var that was never set.
assert "non-numeric positional timeout names the argument" grep -q 'wait-seconds must be a positive integer' <<<"$out"

run_helper_without_pulse 0
assert "non-positive positional timeout rejected as invalid" grep -q 'wait-seconds must be a positive integer' <<<"$out"
assert "non-positive timeout still degrades to exit 0" test "$HELPER_RC" -eq 0

# A digit string long enough wraps in the deadline arithmetic: 2^64+17 reads
# as 17, so the poll's deadline is already in the past and the client is left
# unmuted. The bound is a validation rule, so it has to reject, not clamp.
run_helper_without_pulse 18446744073709551633
assert "a wrapping positional timeout is rejected" grep -q 'wait-seconds must be a positive integer' <<<"$out"
run_helper_without_pulse 9223372036854775807
assert "an intmax positional timeout is rejected" grep -q 'wait-seconds must be a positive integer' <<<"$out"
run_helper_without_pulse 3601
assert "a timeout over the ceiling is rejected" grep -q 'wait-seconds must be a positive integer' <<<"$out"
run_helper_without_pulse 3600
no_wait_warning() { if grep -q 'wait-seconds must be a positive integer' <<<"$out"; then return 1; fi; }
assert "a timeout at the ceiling is accepted" no_wait_warning

set +e
env_out="$(PATH="$NO_PULSE_BIN" CLIENT_MUTE_TIMEOUT=abc "$ROOT/scripts/mute_client_audio.sh" 2>&1)"
set -e
assert "non-numeric env timeout names the env var" grep -q 'CLIENT_MUTE_TIMEOUT must be a positive integer' <<<"$env_out"

run_helper_without_pulse 30 60
assert "a second argument is a usage error" test "$HELPER_RC" -eq 2
assert "the usage error names the argument shape" grep -q 'usage: mute_client_audio.sh \[wait-seconds\]' <<<"$out"

finish
