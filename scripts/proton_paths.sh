#!/usr/bin/env bash
# Single source of truth for Proton prefix (compatdata) resolution across the
# lifecycle scripts: launch_client.sh writes the client log under this prefix,
# and one_shot_join.sh / zero_nre_join_loop.sh read it back. Those must agree
# even when the game lives in a second-disk Steam library, so every script
# resolves through resolve_compat instead of keeping its own copy of the rule.
# Source this file; do not execute it.

# Echo the Proton compatdata prefix for a client install:
#   resolve_compat <game-dir> <steam-appid> <steam-root> [explicit-compat]
# An explicit compat wins; otherwise a game under <library>/steamapps/common/
# derives <library>/steamapps/compatdata/<appid> (second-disk library support);
# anything else falls back to <steam-root>/steamapps/compatdata/<appid>.
resolve_compat() {
  local game="$1" appid="$2" root="$3" compat="${4:-}"
  if [[ -z "$compat" && "$game" == */steamapps/common/* ]]; then
    compat="${game%/common/*}/compatdata/$appid"
  fi
  printf '%s\n' "${compat:-$root/steamapps/compatdata/$appid}"
}

# Kill the leftover Proton/wine stack after the game exe itself is gone:
# orphaned wineservers and pressure-vessel containers leak threads/NPROC
# across cycles until the client wedges at "Initializing Steam", so every
# lifecycle script must sweep the same set instead of keeping its own copy of
# this hard-won list. Best-effort; safe when nothing is running.
kill_wine_stack() {
  pkill -9 -f 'wineserver' 2>/dev/null || true
  pkill -9 -f 'pressure-vessel|pv-adverb|pv-bwrap' 2>/dev/null || true
  pkill -9 -f 'proton.*7DaysToDie|SteamLaunch.*251570' 2>/dev/null || true
}
