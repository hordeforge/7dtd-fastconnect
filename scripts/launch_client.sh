#!/usr/bin/env bash
# Launch stock 7DTD client (Proton) with EAC off. Optional ZDTD_CONNECT auto-join via zdtd-connect mod.
#
# Client audio is muted by default (PipeWire/Pulse sink-input) for automated
# runs. Opt out: CLIENT_MUTE=0 or SEVEN_DAYS_TO_DIE_CLIENT_MUTE=0.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
MUTE_HELPER="$SCRIPT_DIR/mute_client_audio.sh"

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
STEAM_ROOT="${STEAM_ROOT:-$HOME/.local/share/Steam}"
PROTON="${PROTON:-}"
if [[ -z "$PROTON" ]]; then
  # The library holding GAME is searched first, so an install on a second disk
  # finds the Proton next to it rather than only the one in the default root.
  GAME_LIBRARY=""
  if [[ "$GAME" == */steamapps/common/* ]]; then
    GAME_LIBRARY="${GAME%/common/*}"
  fi
  for p in \
    ${GAME_LIBRARY:+"$GAME_LIBRARY/common/Proton - Experimental/proton"} \
    ${GAME_LIBRARY:+"$GAME_LIBRARY/common/Proton 9.0 (Beta)/proton"} \
    "$STEAM_ROOT/steamapps/common/Proton - Experimental/proton" \
    "$STEAM_ROOT/steamapps/common/Proton 9.0 (Beta)/proton" \
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

# Mute client audio by default (opt-out).
MUTE_CLIENT="${CLIENT_MUTE:-${SEVEN_DAYS_TO_DIE_CLIENT_MUTE:-1}}"
case "${MUTE_CLIENT,,}" in
  0 | false | no | off) MUTE_CLIENT="" ;;
esac
MUTE_WAIT="${CLIENT_MUTE_TIMEOUT:-${SEVEN_DAYS_TO_DIE_CLIENT_MUTE_TIMEOUT:-60}}"

# Optional no-Steam client mode (see loadgen/docs/STOCK_AUTH.md Option A):
# CLIENT_PLATFORM=local backs up the game's platform.cfg, selects the Local
# platform with EOS crossplay off, and restores the original on exit. The
# stock dedicated accepts Local clients with no ticket (serverplatforms
# includes Local; loadgen bots ride this path), so the real client can join a
# test server without valid Steam auth and without any server-side bypass mod.
LOCAL_PLATFORM=0
case "${CLIENT_PLATFORM:-}" in
  1 | local | Local | LAN) LOCAL_PLATFORM=1 ;;
esac
PLATFORM_CFG="$GAME/platform.cfg"
PLATFORM_BAK="$GAME/platform.cfg.re-localbak"

swap_local_platform() {
  if [[ ! -f "$PLATFORM_CFG" ]]; then
    echo "WARN: $PLATFORM_CFG missing; cannot switch to Local platform" >&2
    return 0
  fi
  [[ -f "$PLATFORM_BAK" ]] || cp "$PLATFORM_CFG" "$PLATFORM_BAK"
  printf 'platform=Local\ncrossplatform=None\nserverplatforms=Steam,LAN,Local,\n' >"$PLATFORM_CFG"
  echo "Client platform: Local (no Steam auth; restored on exit)"
}

restore_platform() {
  if [[ -f "$PLATFORM_BAK" ]]; then
    mv "$PLATFORM_BAK" "$PLATFORM_CFG"
    echo "Client platform.cfg restored"
  fi
}

if [[ "$LOCAL_PLATFORM" == 1 ]]; then
  swap_local_platform
  trap restore_platform EXIT INT TERM
fi

LOGDIR="$COMPAT/pfx/drive_c/users/steamuser/AppData/Roaming/7DaysToDie/logs"
mkdir -p "$LOGDIR"
LOGFILE="$LOGDIR/output_log_client_zdtd_connect.txt"

if [[ ! -d "$GAME" ]]; then
  echo "Game not found: $GAME" >&2
  exit 1
fi

start_mute_poll() {
  if [[ -z "$MUTE_CLIENT" ]]; then
    return 0
  fi
  if [[ ! -x "$MUTE_HELPER" ]]; then
    chmod +x "$MUTE_HELPER" 2>/dev/null || true
  fi
  if [[ -x "$MUTE_HELPER" ]]; then
    echo "Client mute: on (opt-out CLIENT_MUTE=0); polling up to ${MUTE_WAIT}s"
    # Background: audio stream appears after Unity init, not at process start.
    CLIENT_MUTE_TIMEOUT="$MUTE_WAIT" "$MUTE_HELPER" "$MUTE_WAIT" &
  else
    echo "WARN: mute helper missing ($MUTE_HELPER); client audio not muted." >&2
  fi
}

if [[ -n "$PROTON" && -d "$COMPAT" ]]; then
  export STEAM_COMPAT_DATA_PATH="$COMPAT"
  export STEAM_COMPAT_CLIENT_INSTALL_PATH="${STEAM_COMPAT_CLIENT_INSTALL_PATH:-$STEAM_ROOT}"
  echo "Proton: $PROTON"
  echo "Connect: ${CONNECT:-"(none; use F1 connect after menu)"}"
  echo "Log: $LOGFILE"
  cd "$GAME"
  # Cannot mute after exec — run proton, mute in parallel, wait for the game.
  "$PROTON" run ./7DaysToDie.exe -force-d3d11 -nogs -noeac -logfile "C:/users/steamuser/AppData/Roaming/7DaysToDie/logs/output_log_client_zdtd_connect.txt" "${EXTRA_ARGS[@]}" "$@" &
  game_pid=$!
  start_mute_poll
  wait "$game_pid"
  exit $?
fi

# Fallback: Steam app launch (may still run EAC depending on launcher settings).
echo "Proton not found; using steam -applaunch $STEAM_APPID (set UseEAC false in launcher if needed)"
echo "Connect: ${CONNECT:-"(none)"}"
# Steam does not reliably pass -connect=; env ZDTD_CONNECT is still set for the mod.
export ZDTD_CONNECT="${CONNECT:-}"
steam -applaunch "$STEAM_APPID" -noeac "${EXTRA_ARGS[@]}" "$@" &
steam_pid=$!
start_mute_poll
wait "$steam_pid"
exit $?
