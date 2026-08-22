#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="$ROOT/Source/ConnectMod/ModApi.cs"
FAILS=0

assert() {
    local name="$1"
    shift
    if "$@"; then
        echo "PASS $name"
    else
        echo "FAIL $name" >&2
        FAILS=$((FAILS + 1))
    fi
}

assert "names the opt-in environment variable" grep -q 'PlayerNameEnv = "7DTD_PLAYER_NAME"' "$SOURCE"
assert "reads the requested name before auto-join" grep -q 'ApplyPlayerNameOverride();' "$SOURCE"
assert "uses the stock player-name preference" grep -q 'GamePrefs.Set(EnumGamePrefs.PlayerName, requested)' "$SOURCE"
assert "persists the isolated profile preference" grep -q 'GamePrefs.Instance?.Save();' "$SOURCE"
assert "documents the separate-client mechanism" grep -q 'Local player identity for an isolated test client' "$ROOT/README.md"

if ((FAILS > 0)); then
    echo "RESULT FAIL ($FAILS)" >&2
    exit 1
fi

echo "RESULT PASS"
