#!/usr/bin/env bash
# Structural tests for default-on client mute (no live game required).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FAILS=0
assert() {
	local name="$1"
	shift
	if "$@"; then echo "PASS $name"
	else echo "FAIL $name" >&2; FAILS=$((FAILS + 1)); fi
}

assert "mute helper executable" test -x "$ROOT/scripts/mute_client_audio.sh"
assert "launch_client references mute helper" grep -q 'mute_client_audio.sh' "$ROOT/scripts/launch_client.sh"
assert "launch defaults mute on" grep -qE 'CLIENT_MUTE:-.*1|SEVEN_DAYS_TO_DIE_CLIENT_MUTE:-1' "$ROOT/scripts/launch_client.sh"
assert "launch documents opt-out" grep -q 'CLIENT_MUTE=0' "$ROOT/README.md" || grep -q 'opt-out' "$ROOT/scripts/launch_client.sh"
assert "launch does not exec proton (mute needs wait)" ! grep -qE 'exec "\$PROTON"' "$ROOT/scripts/launch_client.sh"
assert "launch backgrounds mute poll" grep -q 'start_mute_poll' "$ROOT/scripts/launch_client.sh"

if ((FAILS > 0)); then
	echo "RESULT FAIL ($FAILS)" >&2
	exit 1
fi
echo "RESULT PASS"
exit 0
