#!/usr/bin/env bash
# Structural tests for the 7DTD_PLAYER_NAME override (no live game required).
# Greps that could match elsewhere in ModApi.cs (GamePrefs.Instance?.Save() is
# not unique to this method) are scoped to the ApplyPlayerNameOverride body, so
# an unrelated occurrence cannot satisfy them.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="$ROOT/Source/ConnectMod/ModApi.cs"
source "$ROOT/scripts/test_common.sh"

METHOD="$(sed -n '/static void ApplyPlayerNameOverride()/,/^        }$/p' "$SOURCE")"
assert "ApplyPlayerNameOverride method exists" test -n "$METHOD"

body_contains() { [[ "$METHOD" == *"$1"* ]]; }

assert "names the opt-in environment variable" grep -q 'PlayerNameEnv = "7DTD_PLAYER_NAME"' "$SOURCE"
assert "reads the requested name before auto-join" grep -q 'ApplyPlayerNameOverride();' "$SOURCE"
assert "falls back to a generated name when the variable is unset" \
	body_contains 'PlayerNames.Resolve()'
assert "clamps an over-long requested name" \
	body_contains 'PlayerNames.MaxLength'
assert "uses the stock player-name preference inside the override" \
	body_contains 'GamePrefs.Set(EnumGamePrefs.PlayerName, requested)'
assert "persists the preference inside the override" \
	body_contains 'GamePrefs.Instance?.Save();'
assert "documents the separate-client mechanism" grep -q 'Local player identity for an isolated test client' "$ROOT/README.md"

finish
