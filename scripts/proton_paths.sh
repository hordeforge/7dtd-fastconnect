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

# Echo the Windows account name a Proton prefix runs as: the "users/<name>"
# component every log path in this repo is built from.
#   resolve_prefix_user <compatdata-prefix>
# Proton creates pfx/drive_c/users/steamuser, so that is the answer whenever it
# is there. A prefix created under a different account (a hand-made one, or one
# a launcher built with its own user name) has exactly one other user dir, and
# that is the one the client writes its log to: naming steamuser there made the
# launcher mkdir a log path no client ever writes and the join harnesses poll an
# empty file, reporting a timeout for a cycle that had joined. With no
# single answer, steamuser stays the value, so a fresh prefix behaves as before.
resolve_prefix_user() {
  local compat="${1:?resolve_prefix_user: compatdata prefix required}"
  local users="$1/pfx/drive_c/users" found=() d
  if [[ -d "$users/steamuser" ]]; then
    printf 'steamuser\n'
    return 0
  fi
  if [[ -d "$users" ]]; then
    for d in "$users"/*/; do
      if [[ -d "$d" ]]; then found+=("${d%/}"); fi
    done
  fi
  if ((${#found[@]} == 1)); then
    printf '%s\n' "${found[0]##*/}"
  else
    printf 'steamuser\n'
  fi
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
