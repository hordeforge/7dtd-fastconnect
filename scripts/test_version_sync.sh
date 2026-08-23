#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
XML="$ROOT/ModInfo.xml"
CS="$ROOT/Source/ConnectMod/ModApi.cs"
source "$ROOT/scripts/test_common.sh"

xml_version() {
    sed -n 's/.*<Version value="\([^"]*\)".*/\1/p' "$XML" | head -1
}

cs_version() {
    sed -n 's/.*public const string Version = "\([^"]*\)".*/\1/p' "$CS" | head -1
}

assert "parses ModInfo.xml version" test -n "$(xml_version)"
assert "parses ModApi.cs version" test -n "$(cs_version)"
assert "ModInfo.xml and ModApi versions agree" \
    test "$(xml_version)" = "$(cs_version)"

finish
