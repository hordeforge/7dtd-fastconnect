#!/usr/bin/env bash
# Launch stock 7DTD client (Proton) with EAC off. Optional 7DTD_CONNECT auto-join via the connect mod.
#
# Client audio is muted by default (PipeWire/Pulse sink-input) for automated
# runs. Opt out: CLIENT_MUTE=0 or SEVEN_DAYS_TO_DIE_CLIENT_MUTE=0.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
MUTE_HELPER="$SCRIPT_DIR/mute_client_audio.sh"
source "$SCRIPT_DIR/proton_paths.sh"

GAME="${GAME:-$HOME/.local/share/Steam/steamapps/common/7 Days To Die}"
STEAM_APPID="${STEAM_APPID:-251570}"
# Prefer Proton Experimental / GE if present; fall back to steam launch.
STEAM_ROOT="${STEAM_ROOT:-$HOME/.local/share/Steam}"
# Derive the Proton prefix from GAME, so a library on another disk works. A
# hardcoded default path silently falls through to the `steam -applaunch`
# branch below on such an install, which loses the environment this script was
# given -- and passing 7DTD_CONNECT or a playtest suite variable through the
# environment is the whole point of launching Proton directly. The harnesses
# (one_shot_join.sh) read the client log back from this same resolved prefix,
# so the rule lives once in proton_paths.sh.
COMPAT="$(resolve_compat "$GAME" "$STEAM_APPID" "$STEAM_ROOT" "${COMPAT:-}")"
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

# Bash cannot expand or export a variable name starting with a digit, so read
# the canonical 7DTD_CONNECT via printenv.
CONNECT="$(printenv 7DTD_CONNECT 2>/dev/null || true)"
# Always skip TFP intro splash (before mods load) and stock news launch pref.
EXTRA_ARGS=(-skipintro -SkipNewsScreen=true)
if [[ -n "$CONNECT" ]]; then
  EXTRA_ARGS+=(-connect="$CONNECT")
fi

# Mute client audio by default (opt-out).
MUTE_CLIENT="${CLIENT_MUTE:-${SEVEN_DAYS_TO_DIE_CLIENT_MUTE:-1}}"
case "${MUTE_CLIENT,,}" in
  0 | false | no | off) MUTE_CLIENT="" ;;
esac
MUTE_WAIT="${CLIENT_MUTE_TIMEOUT:-${SEVEN_DAYS_TO_DIE_CLIENT_MUTE_TIMEOUT:-60}}"

# Optional no-Steam client mode (see ../7dtd-loadgen/docs/STOCK_AUTH.md Option A):
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
  # A previous hard-killed run (SIGKILL cannot be trapped) may have left the
  # config swapped with a backup behind; restore it first so the swap is
  # idempotent and self-healing.
  if [[ -f "$PLATFORM_BAK" ]]; then
    mv "$PLATFORM_BAK" "$PLATFORM_CFG"
    echo "Client platform.cfg restored from a previous interrupted run"
  fi
  if [[ ! -f "$PLATFORM_CFG" ]]; then
    echo "WARN: $PLATFORM_CFG missing; cannot switch to Local platform" >&2
    return 0
  fi
  cp "$PLATFORM_CFG" "$PLATFORM_BAK"
  printf 'platform=Local\ncrossplatform=None\nserverplatforms=Steam,LAN,Local,\n' >"$PLATFORM_CFG"
  echo "Client platform: Local (no Steam auth; restored on exit)"
}

restore_platform() {
  if [[ -f "$PLATFORM_BAK" ]]; then
    mv "$PLATFORM_BAK" "$PLATFORM_CFG"
    echo "Client platform.cfg restored"
  fi
}

if [[ ! -d "$GAME" ]]; then
  echo "Game not found: $GAME" >&2
  exit 1
fi

MUTE_PID=""
# PID of the direct-Proton game child this script waits on. INT/TERM forward
# to it so a stop aimed at the launcher cannot orphan the wine/Proton stack
# behind it; the EXIT trap still reaps the mute poller and restores
# platform.cfg afterwards. The steam -applaunch fallback never registers here:
# that pid is the shared desktop Steam client, not a child this script owns.
GAME_PID=""
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
    MUTE_PID=$!
  else
    echo "WARN: mute helper missing ($MUTE_HELPER); client audio not muted." >&2
  fi
}

# The poller is only useful while the game runs; stop and reap it so it does
# not outlive this script still polling pactl for a dead client.
stop_mute_poll() {
  if [[ -n "$MUTE_PID" ]] && kill -0 "$MUTE_PID" 2>/dev/null; then
    kill "$MUTE_PID" 2>/dev/null || true
  fi
  if [[ -n "$MUTE_PID" ]]; then
    wait "$MUTE_PID" 2>/dev/null || true
    MUTE_PID=""
  fi
}

# One cleanup path for every exit route: normal completion, a set -e abort,
# and INT/TERM (one_shot_join.sh stops launchers with TERM). Without the
# exit-forwarding traps a bare TERM would kill the script mid-wait, leaving
# the mute poller running its full window and, in Local-platform mode,
# platform.cfg swapped. restore_platform is a no-op without a backup file.
on_exit() {
  stop_mute_poll
  restore_platform
}
trap on_exit EXIT
# Forward to the game child first (no-op when it already exited, as when
# one_shot_join.sh kills clients before stopping this launcher), then take the
# normal exit path so on_exit still runs.
on_signal() {
  if [[ -n "$GAME_PID" ]]; then
    kill -TERM "$GAME_PID" 2>/dev/null || true
  fi
  exit "$1"
}
trap 'on_signal 130' INT
trap 'on_signal 143' TERM

# Side effects start only below the traps: a failure between the swap and the
# old trap installation point (mkdir -p LOGDIR under set -e) used to leave
# platform.cfg swapped with no restore until the next launch self-healed it.
if [[ "$LOCAL_PLATFORM" == 1 ]]; then
  swap_local_platform
fi

LOGDIR="$COMPAT/pfx/drive_c/users/steamuser/AppData/Roaming/7DaysToDie/logs"
mkdir -p "$LOGDIR"
# Same file as WIN_LOGFILE below: LOGFILE is the prefix-side path, WIN_LOGFILE
# the in-guest path handed to -logfile.
LOGFILE="$LOGDIR/output_log_client_7dtd_connect.txt"
WIN_LOGFILE="C:/users/steamuser/AppData/Roaming/7DaysToDie/logs/output_log_client_7dtd_connect.txt"

if [[ -n "$PROTON" && -d "$COMPAT" ]]; then
  export STEAM_COMPAT_DATA_PATH="$COMPAT"
  export STEAM_COMPAT_CLIENT_INSTALL_PATH="${STEAM_COMPAT_CLIENT_INSTALL_PATH:-$STEAM_ROOT}"
  echo "Proton: $PROTON"
  echo "Connect: ${CONNECT:-"(none; use F1 connect after menu)"}"
  echo "Log: $LOGFILE"
  cd "$GAME"
  # Cannot mute after exec — run proton, mute in parallel, wait for the game.
  env 7DTD_CONNECT="${CONNECT:-}" "$PROTON" run ./7DaysToDie.exe -force-d3d11 -nogs -noeac -logfile "$WIN_LOGFILE" "${EXTRA_ARGS[@]}" "$@" &
  game_pid=$!
  GAME_PID="$game_pid"
  start_mute_poll
  launch_status=0
  wait "$game_pid" || launch_status=$?
  exit "$launch_status"
fi

# Fallback: Steam app launch (may still run EAC depending on launcher settings).
echo "Proton not found; using steam -applaunch $STEAM_APPID (set UseEAC false in launcher if needed)"
echo "Connect: ${CONNECT:-"(none)"}"
# Steam does not reliably pass -connect=; pass the canonical name through
# `env` because bash cannot export a name starting with a digit.
env 7DTD_CONNECT="${CONNECT:-}" steam -applaunch "$STEAM_APPID" -noeac "${EXTRA_ARGS[@]}" "$@" &
steam_pid=$!
start_mute_poll
launch_status=0
wait "$steam_pid" || launch_status=$?
exit "$launch_status"
