#!/usr/bin/env bash
# Launch stock 7DTD client (Proton) with EAC off. Optional ZDTD_CONNECT auto-join via zdtd-connect mod.
set -euo pipefail

GAME="${GAME:-$HOME/.local/share/Steam/steamapps/common/7 Days To Die}"
STEAM_APPID="${STEAM_APPID:-251570}"
# Derive the Proton prefix from GAME, so a library on another disk works. A
# hardcoded default path silently falls through to the `steam -applaunch`
# branch below on such an install, which loses the environment this script was
# given -- and passing ZDTD_CONNECT or a playtest suite variable through the
# environment is the whole point of launching Proton directly.
COMPAT="${COMPAT:-}"
if [[ -z "$COMPAT" && "$GAME" == */steamapps/common/* ]]; then
  COMPAT="${GAME%/common/*}/compatdata/$STEAM_APPID"
fi
COMPAT="${COMPAT:-$HOME/.local/share/Steam/steamapps/compatdata/$STEAM_APPID}"
# Prefer Proton Experimental / GE if present; fall back to steam launch.
PROTON="${PROTON:-}"
if [[ -z "$PROTON" ]]; then
  for p in \
    "$HOME/.local/share/Steam/steamapps/common/Proton - Experimental/proton" \
    "$HOME/.local/share/Steam/steamapps/common/Proton 9.0 (Beta)/proton" \
    "$HOME/.steam/steam/steamapps/common/Proton - Experimental/proton"
  do
    if [[ -x "$p" ]]; then PROTON="$p"; break; fi
  done
fi

CONNECT="${ZDTD_CONNECT:-}"
if [[ -z "$CONNECT" ]]; then
  CONNECT="$(printenv 7DTD_CONNECT 2>/dev/null || true)"
fi
# Always skip TFP intro splash (before mods load) and stock news launch pref.
EXTRA_ARGS=(-skipintro -SkipNewsScreen=true)
if [[ -n "$CONNECT" ]]; then
  EXTRA_ARGS+=(-connect="$CONNECT")
  export ZDTD_CONNECT="$CONNECT"
fi

LOGDIR="$COMPAT/pfx/drive_c/users/steamuser/AppData/Roaming/7DaysToDie/logs"
mkdir -p "$LOGDIR"
LOGFILE="$LOGDIR/output_log_client_zdtd_connect.txt"

if [[ ! -d "$GAME" ]]; then
  echo "Game not found: $GAME" >&2
  exit 1
fi

if [[ -n "$PROTON" && -d "$COMPAT" ]]; then
  export STEAM_COMPAT_DATA_PATH="$COMPAT"
  export STEAM_COMPAT_CLIENT_INSTALL_PATH="${STEAM_COMPAT_CLIENT_INSTALL_PATH:-$HOME/.local/share/Steam}"
  echo "Proton: $PROTON"
  echo "Connect: ${CONNECT:-"(none; use F1 connect after menu)"}"
  echo "Log: $LOGFILE"
  cd "$GAME"
  exec "$PROTON" run ./7DaysToDie.exe -force-d3d11 -nogs -noeac -logfile "C:/users/steamuser/AppData/Roaming/7DaysToDie/logs/output_log_client_zdtd_connect.txt" "${EXTRA_ARGS[@]}" "$@"
fi

# Fallback: Steam app launch (may still run EAC depending on launcher settings).
echo "Proton not found; using steam -applaunch $STEAM_APPID (set UseEAC false in launcher if needed)"
echo "Connect: ${CONNECT:-"(none)"}"
# Steam does not reliably pass -connect=; env ZDTD_CONNECT is still set for the mod.
export ZDTD_CONNECT="${CONNECT:-}"
exec steam -applaunch "$STEAM_APPID" -noeac "${EXTRA_ARGS[@]}" "$@"
