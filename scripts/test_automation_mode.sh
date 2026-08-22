#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MODE="$ROOT/Source/ConnectMod/AutomationMode.cs"
API="$ROOT/Source/ConnectMod/ModApi.cs"
PATCHES="$ROOT/Source/ConnectMod/SkipIntroPatches.cs"

grep -q 'EnvVar = "7DTD_CONNECT_AUTOMATION"' "$MODE"
grep -q 'return ConnectTarget.TryFromLaunchContext' "$MODE"
grep -q 'if (!AutomationMode.Enabled' "$API"
grep -q 'typeof(AutomationPatchAttribute)' "$API"
grep -q '\[AutomationPatch\]' "$PATCHES"
test "$(grep -c '^[[:space:]]*\[AutomationPatch\]' "$PATCHES")" \
    -eq "$(grep -c '^[[:space:]]*\[HarmonyPatch' "$PATCHES")"
grep -q 'A regular client launch still loads the' "$ROOT/README.md"

echo "RESULT PASS"
