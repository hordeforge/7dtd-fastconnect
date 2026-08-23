#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MODE="$ROOT/Source/ConnectMod/AutomationMode.cs"
API="$ROOT/Source/ConnectMod/ModApi.cs"
PATCHES="$ROOT/Source/ConnectMod/SkipIntroPatches.cs"
source "$ROOT/scripts/test_common.sh"

assert "names the automation env var" grep -q 'EnvVar = "7DTD_CONNECT_AUTOMATION"' "$MODE"
assert "derives default from the launch context" grep -q 'return ConnectTarget.TryFromLaunchContext' "$MODE"
assert "gates auto-join on automation mode" grep -q 'if (!AutomationMode.Enabled' "$API"
assert "marks automation patches in ModApi" grep -q 'typeof(AutomationPatchAttribute)' "$API"
assert "tags patches with AutomationPatch" grep -q '\[AutomationPatch\]' "$PATCHES"
assert "every Harmony patch is automation-gated" \
	test "$(grep -c '^[[:space:]]*\[AutomationPatch\]' "$PATCHES")" \
	-eq "$(grep -c '^[[:space:]]*\[HarmonyPatch' "$PATCHES")"
assert "documents regular-client mode" grep -q 'A regular client launch still loads the' "$ROOT/README.md"

finish
