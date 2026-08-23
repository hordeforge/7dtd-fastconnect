#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="$ROOT/Source/ConnectMod/ModApi.cs"
source "$ROOT/scripts/test_common.sh"

assert "names the opt-in environment variable" grep -q 'PlayerNameEnv = "7DTD_PLAYER_NAME"' "$SOURCE"
assert "reads the requested name before auto-join" grep -q 'ApplyPlayerNameOverride();' "$SOURCE"
assert "uses the stock player-name preference" grep -q 'GamePrefs.Set(EnumGamePrefs.PlayerName, requested)' "$SOURCE"
assert "persists the isolated profile preference" grep -q 'GamePrefs.Instance?.Save();' "$SOURCE"
assert "documents the separate-client mechanism" grep -q 'Local player identity for an isolated test client' "$ROOT/README.md"

finish
