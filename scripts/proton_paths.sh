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
