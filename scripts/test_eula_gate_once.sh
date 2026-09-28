#!/usr/bin/env bash
# Offline gate for EulaSkip's EULA-gate handling: the gate is opened by name
# through GUIWindowManager.Open, so one session can request it more than once.
# Every request accepted the EULA (a GamePrefs save to disk) and re-fired
# ModEvents.MainMenuOpened at every mod, for a gate already resolved. The
# handler must latch after the first request, before any repeat, and a repeat
# must still block the window.
#
# The mod itself is never executed here: it loads inside the game.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

EULA="$ROOT/Source/ConnectMod/EulaSkip.cs"
PATCHES="$ROOT/Source/ConnectMod/SkipIntroPatches.cs"

line_of() { grep -n -m1 -F -- "$1" "$EULA" | cut -d: -f1; }

# The latch must be read before the prefs accept, or a repeat would still
# write them.
latch_precedes_work() {
	local guard accept
	guard="$(line_of 'if (_gateHandled) return false;')" || return 1
	accept="$(line_of 'AcceptLatest();')" || return 1
	(( guard < accept ))
}

# And set after the dispatch, so a repeat cannot skip the first attempt.
latch_follows_dispatch() {
	local dispatch latch
	dispatch="$(line_of 'ModEvents.MainMenuOpened.Invoke(ref data);')" || return 1
	latch="$(line_of '_gateHandled = true;')" || return 1
	(( dispatch < latch ))
}

# A repeat returns false, the same verdict as the first request: the window
# stays blocked either way.
repeat_still_blocks() {
	grep -q 'if (_gateHandled) return false;' "$EULA"
}

# Both GUIWindowManager.Open arities that can name the gate go through the one
# latched body; a second, unlatched copy would undo the latch.
single_latched_entry() {
	[[ "$(grep -c 'EulaSkip.BlockGateWindow(' "$PATCHES")" -eq 2 ]] &&
		[[ "$(grep -c 'EulaSkip.GateWindowName' "$PATCHES")" -eq 2 ]]
}

assert "reads the latch before accepting the EULA" latch_precedes_work
assert "latches after the MainMenuOpened dispatch" latch_follows_dispatch
assert "a repeat request still blocks the window" repeat_still_blocks
assert "both Open arities share the latched body" single_latched_entry

finish
