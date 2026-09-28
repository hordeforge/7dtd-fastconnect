#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
XML="$ROOT/ModInfo.xml"
CS="$ROOT/Source/ConnectMod/ModApi.cs"
CS_TARGET="$ROOT/Source/ConnectMod/ConnectTarget.cs"
PYPROJECT="$ROOT/pyproject.toml"
source "$ROOT/scripts/test_common.sh"

xml_version() {
    sed -n 's/.*<Version value="\([^"]*\)".*/\1/p' "$XML" | head -1
}

cs_version() {
    sed -n 's/.*public const string Version = "\([^"]*\)".*/\1/p' "$CS" | head -1
}

pyproject_version() {
    # The dev-tooling package carries the mod version so release bumps touch
    # one number everywhere; a lagging value here is how 0.10.4 drifted while
    # ModInfo/ModApi moved to 0.10.5.
    sed -n 's/^version = "\(.*\)"$/\1/p' "$PYPROJECT" | head -1
}

cs_game_version() {
    sed -n 's/.*PlaceholderServerVersion = "\([^"]*\)".*/\1/p' "$CS_TARGET" | head -1
}

pyproject_game_version() {
    # The game build the client advertises when its own version read is
    # blocked. It lives in the mod only, so without a second record here a
    # game update would leave a stale version on the wire and nothing would
    # say so.
    sed -n 's/^game-version = "\(.*\)"$/\1/p' "$PYPROJECT" | head -1
}

assert "parses ModInfo.xml version" test -n "$(xml_version)"
assert "parses ModApi.cs version" test -n "$(cs_version)"
assert "parses pyproject.toml version" test -n "$(pyproject_version)"
assert "ModInfo.xml and ModApi versions agree" \
    test "$(xml_version)" = "$(cs_version)"
assert "pyproject version agrees with the mod version" \
    test "$(pyproject_version)" = "$(xml_version)"
assert "parses the ConnectTarget server-version placeholder" test -n "$(cs_game_version)"
assert "parses the pyproject game version" test -n "$(pyproject_game_version)"
assert "the advertised game version matches the recorded one" \
    test "$(cs_game_version)" = "$(pyproject_game_version)"

finish
