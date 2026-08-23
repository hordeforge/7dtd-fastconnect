#!/usr/bin/env bash
# Structural + behavioral tests for default-on client mute (no live game or
# audio server required): launch wiring is grepped, and mute_client_audio.sh
# itself is exercised against a stub pactl so the jq stream-matching filter
# actually runs.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"
not_grep() {
	local pattern="$1" file="$2"
	! grep -q -- "$pattern" "$file"
}

assert "mute helper executable" test -x "$ROOT/scripts/mute_client_audio.sh"
assert "launch_client references mute helper" grep -q 'mute_client_audio.sh' "$ROOT/scripts/launch_client.sh"
assert "launch defaults mute on" grep -qE 'CLIENT_MUTE:-.*1|SEVEN_DAYS_TO_DIE_CLIENT_MUTE:-1' "$ROOT/scripts/launch_client.sh"
assert "launch documents opt-out" grep -q 'CLIENT_MUTE=0' "$ROOT/README.md" || grep -q 'opt-out' "$ROOT/scripts/launch_client.sh"
if grep -qE 'exec "\$PROTON"' "$ROOT/scripts/launch_client.sh"; then
	echo "FAIL launch does not exec proton (mute needs wait)" >&2
	FAILS=$((FAILS + 1))
else
	echo "PASS launch does not exec proton (mute needs wait)"
fi
assert "launch backgrounds mute poll" grep -q 'start_mute_poll' "$ROOT/scripts/launch_client.sh"

# Behavioral: the helper's jq filter must mute streams whose application.name
# or (case-insensitive) process binary matches 7DaysToDie, and nothing else.
if command -v jq >/dev/null 2>&1; then
	BEHAV="$(mktemp -d "${TMPDIR:-/tmp}/mute-helper.XXXXXX")"
	trap 'rm -rf "$BEHAV"' EXIT
	cat > "$BEHAV/pactl" <<'STUB'
#!/usr/bin/env bash
case "$1" in
	-f) cat "${PACTL_JSON:?}" ;;
	set-sink-input-mute) printf '%s\n' "$*" >>"${PACTL_LOG:?}" ;;
	*) echo "unexpected pactl call: $*" >&2; exit 1 ;;
esac
STUB
	chmod +x "$BEHAV/pactl"

	printf '%s\n' '[
	 {"index": 7, "properties": {"application.name": "7DaysToDie"}},
	 {"index": 9, "properties": {"application.name": "spotify"}},
	 {"index": 11, "properties": {"application.process.binary": "7daystodie.exe"}}
	]' > "$BEHAV/streams.json"
	: > "$BEHAV/mute.log"
	if PATH="$BEHAV:$PATH" PACTL_JSON="$BEHAV/streams.json" PACTL_LOG="$BEHAV/mute.log" \
		"$ROOT/scripts/mute_client_audio.sh" 5 >"$BEHAV/out" 2>"$BEHAV/err"; then
		assert "mutes stream matched by application name" grep -qx 'set-sink-input-mute 7 1' "$BEHAV/mute.log"
		assert "mutes stream matched case-insensitively by binary" grep -qx 'set-sink-input-mute 11 1' "$BEHAV/mute.log"
		assert "leaves unrelated streams unmuted" not_grep ' 9 ' "$BEHAV/mute.log"
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

finish
