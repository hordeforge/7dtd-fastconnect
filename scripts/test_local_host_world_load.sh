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
# Startup tracing is opt-in (`diag on`); the fix itself is the async drain.
grep -q 'DiagToggle.Enabled || step <= UngatedTraceSteps' "$PATCHES"
grep -q 'PendingLoadCount' "$PATCHES"
grep -q 'deferedLoadRequests' "$PATCHES"
! grep -q 'LocalHostSpawnTrace' "$ROOT/Source/ConnectMod/ModApi.sh" 2>/dev/null
! test -f "$ROOT/Source/ConnectMod/LocalHostSpawnTrace.cs"
# Async loads starve under Proton at the stock Low priority, and Unity throttles
# an unfocused window; both are scoped to the load and restored afterwards.
grep -q 'backgroundLoadingPriority = ThreadPriority.High' "$PATCHES"
grep -q 'runInBackground = true' "$PATCHES"
grep -q 'backgroundLoadingPriority = previousLoadPriority' "$PATCHES"
grep -q 'runInBackground = previousRunInBackground' "$PATCHES"
grep -q 'showOpenerMovieOnLoad = false' "$API"
grep -q 'OptionsIntroMovieEnabled, false' "$API"
grep -q 'offline Local-platform world initialization' "$ROOT/README.md"
grep -q 'unnecessary for ordinary Steam play' "$ROOT/README.md"

echo "RESULT PASS"
