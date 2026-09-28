#!/usr/bin/env bash
# Offline gate for ModApi's one-shot auto-join latch (_autoTried).
#
# MainMenuOpened fires again every time the client returns to the main menu
# (a failed join, a disconnect, a rejected EULA window that re-opens the menu),
# and the latch is what keeps the auto-join from re-reading 7DTD_CONNECT and
# re-connecting on each of those. It is also the one attempt the session has:
# armed on entry, a launch-context resolution that throws spends the attempt
# without having made one, and every later menu open returns at the latch. The
# client then never auto-joins for the rest of the session, and the only trace
# is the probe's first-failure notice.
#
# The mod itself is never executed here: it loads inside the game.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

API="$ROOT/Source/ConnectMod/ModApi.cs"

line_of() { grep -n -m1 -F -- "$1" "$API" | cut -d: -f1; }

# The latch is read, then the launch context is resolved, then the latch is
# armed: that order is the whole contract.
latch_armed_after_resolution() {
	local guard resolve arm
	guard="$(line_of 'if (_autoTried) return;')" || return 1
	resolve="$(line_of 'ConnectTarget.TryFromLaunchContext(out host, out port, out source);')" || return 1
	arm="$(line_of '_autoTried = true;')" || return 1
	(( guard < resolve && resolve < arm ))
}

# Exactly one arming site, or a second one could spend the attempt before the
# resolution it depends on.
single_arming_site() { [[ "$(grep -c '_autoTried = true;' "$API")" -eq 1 ]]; }

# A throw out of the resolution leaves the latch unset: the handler returns
# from inside its own catch, and the catch does not arm anything.
throwing_resolution_is_retryable() {
	local tryStart catchEnd arm
	tryStart="$(line_of 'haveTarget = ConnectTarget.TryFromLaunchContext')" || return 1
	catchEnd="$(line_of 'ProbeFailure.Once("auto-join target", ex);')" || return 1
	arm="$(line_of '_autoTried = true;')" || return 1
	(( tryStart < catchEnd && catchEnd < arm ))
}

# "no usable target" is a decision, not a failure, so it latches as well:
# re-reading the env on every menu open would repeat the idle line. The arm
# must sit above the idle branch, not merely exist somewhere in the file.
idle_latches() {
	local arm idle
	arm="$(line_of '_autoTried = true;')" || return 1
	idle="$(line_of 'auto-join idle (no usable')" || return 1
	(( arm < idle ))
}

assert "arms the latch after the launch context resolves" latch_armed_after_resolution
assert "arms the latch from one place" single_arming_site
assert "a throwing resolution leaves the latch unset" throwing_resolution_is_retryable
assert "the idle path latches too" idle_latches

finish
