#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PATCHES="$ROOT/Source/ConnectMod/LocalHostWorldLoadPatches.cs"
API="$ROOT/Source/ConnectMod/ModApi.cs"
source "$ROOT/scripts/test_common.sh"

# `assert` runs its command with "$@", which cannot carry a leading `!`.
lacks() { ! grep -q "$1" "$PATCHES"; }

assert "runs only outside automation mode" grep -q '!AutomationMode.Enabled' "$PATCHES"
assert "runs only on a host" grep -q 'Instance?.IsServer' "$PATCHES"
assert "wraps World.LoadWorld" grep -q 'typeof(World), nameof(World.LoadWorld)' "$PATCHES"
assert "wraps createWorld" grep -q 'typeof(GameManager), "createWorld"' "$PATCHES"
assert "wraps StartAsServer" grep -q 'typeof(GameManager), nameof(GameManager.StartAsServer)' "$PATCHES"
assert "is not automation-gated" lacks '\[AutomationPatch\]'
# The fix: drain LoadManager's async queue before player creation, then hold
# sync loading, so the synchronous SDCS player-part loads cannot hit
# Addressables.WaitForCompletion() while an async operation is in flight.
assert "counts pending async loads" grep -q 'deferedLoadRequests' "$PATCHES"
assert "drains before player creation" grep -q 'draining .* pending async loads' "$PATCHES"
assert "holds sync loading after the drain" grep -q 'HoldForceLoadSync()' "$PATCHES"
assert "releases sync loading when startup ends" grep -q 'ReleaseForceLoadSync()' "$PATCHES"
assert "restores the previous forceLoadSync in World.LoadWorld" \
	grep -q 'forceSync.SetValue(null, previousForceSync)' "$PATCHES"
# Async loads starve under Proton at the stock Low priority, and Unity throttles
# an unfocused window; both are scoped to the load and restored afterwards.
assert "raises load priority" grep -q 'backgroundLoadingPriority = ThreadPriority.High' "$PATCHES"
assert "restores load priority" grep -q 'backgroundLoadingPriority = previousLoadPriority' "$PATCHES"
assert "restores runInBackground" grep -q 'runInBackground = previousRunInBackground' "$PATCHES"
assert "leaves vsync and the frame cap to the player" lacks 'BootUnblock.ApplyFrameUncap('
# Both a Harmony prefix on PlayerMoveController.Update and a component
# disable/re-enable stalled startup after "createWorld() done"; the transient
# NullReferenceExceptions it chased are preferable to that.
assert "never touches PlayerMoveController" lacks 'PlayerMoveController'
# Traces and the hitch monitor are opt-in; the fix itself is the async drain.
assert "gates the startup trace behind diag" grep -q 'if (DiagToggle.Enabled)' "$PATCHES"
assert "gates hitch logging behind diag" grep -q '!DiagToggle.Enabled) continue' "$PATCHES"
assert "documents the fix" \
	grep -q 'offline Local-platform world initialization' "$ROOT/README.md"
assert "documents that ordinary play needs no sync-load opt-out" \
	grep -q 'unnecessary for ordinary Steam play' "$ROOT/README.md"
assert "disables the intro movie preference" grep -q 'OptionsIntroMovieEnabled, false' "$API"
assert "clears showOpenerMovieOnLoad" grep -q 'showOpenerMovieOnLoad = false' "$API"
assert "logs under the mod-wide prefix" lacks '7dtd-connect\]'

finish
