#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PATCHES="$ROOT/Source/ConnectMod/LocalHostWorldLoadPatches.cs"
API="$ROOT/Source/ConnectMod/ModApi.cs"

grep -q '!AutomationMode.Enabled' "$PATCHES"
grep -q 'Instance?.IsServer' "$PATCHES"
grep -q 'typeof(World), nameof(World.LoadWorld)' "$PATCHES"
grep -q 'typeof(GameManager), "createWorld"' "$PATCHES"
grep -q 'typeof(GameManager), nameof(GameManager.StartAsServer)' "$PATCHES"
! grep -q 'typeof(PlayerMoveController), nameof(PlayerMoveController.Update)' "$PATCHES"
grep -q 'forceSync.SetValue(null, previousForceSync)' "$PATCHES"
grep -q 'CreateWorldUnsafeFrameBreaks = 9' "$PATCHES"
! grep -q 'primaryUI?.xui?.IsReady' "$PATCHES"
# The known-good world-load path touches PlayerMoveController in no way at all:
# both a Harmony prefix and a component disable/re-enable stalled startup after
# createWorld() done. Transient NullReferenceExceptions are preferred to that.
! grep -q 'PlayerMoveController' "$PATCHES"
! grep -q 'ThreadManager.StartCoroutine' "$PATCHES"
# Stall tracing must stay opt-in so normal play is unaffected.
grep -q 'DiagToggle.Enabled' "$PATCHES"
grep -q 'StartAsServer trace' "$PATCHES"
grep -q 'showOpenerMovieOnLoad = false' "$API"
grep -q 'OptionsIntroMovieEnabled, false' "$API"
grep -q 'offline Local-platform world initialization' "$ROOT/README.md"
grep -q 'unnecessary for ordinary Steam play' "$ROOT/README.md"

echo "RESULT PASS"
