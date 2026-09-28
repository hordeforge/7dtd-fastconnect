#!/usr/bin/env bash
# Launch stock 7DTD client (Proton) with EAC off. Optional 7DTD_CONNECT auto-join via the connect mod.
#
# Client audio is muted by default (PipeWire/Pulse sink-input) for automated
# runs. Opt out: CLIENT_MUTE=0 or SEVEN_DAYS_TO_DIE_CLIENT_MUTE=0.
set -euo pipefail

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  cat <<'EOF'
Usage: launch_client.sh [extra args passed to 7DaysToDie.exe]

Launch the stock 7DTD client under Proton with EAC off (-noeac), skipping
the intro splash and news screen. Auto-joins a server when a connect target
is set; otherwise use F1 -> connect after the main menu opens.

    env 7DTD_CONNECT=127.0.0.1:27025 ./scripts/launch_client.sh

Exit status: 2 usage error, 1 setup failure (missing game, no usable
Proton), otherwise the game or Steam client's own exit status. Any extra
args are forwarded to the game executable.

Key env vars (full table: README "Environment variables"):
  7DTD_CONNECT         host[:port] auto-join target once the main menu opens
  GAME                 client install dir (default: stock Steam path)
  PROTON / COMPAT      Proton binary / compatdata prefix overrides
  GFX_API              d3d11 (default) | d3d12 | vulkan | glcore | none;
                       an invalid value aborts before launch
  CLIENT_MUTE          1 (default) mutes the game audio stream at the OS
                       audio level; 0/false/no/off keeps sound on, and any
                       other non-empty value warns and mutes
  CLIENT_MUTE_TIMEOUT  seconds to poll for that stream, 1..3600 (default 60)
  MUTE_POLL_STOP_GRACE_SEC
                       seconds the mute poller gets to exit on shutdown
                       before it is killed (default 5)
  CLIENT_PLATFORM      local | lan | 1 selects no-Steam Local mode
EOF
  exit 0
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
MUTE_HELPER="$SCRIPT_DIR/mute_client_audio.sh"
source "$SCRIPT_DIR/proton_paths.sh"
# Shared value checks (trim, lower, env_bool): see scripts/config_validate.sh.
source "$SCRIPT_DIR/config_validate.sh"
# Log-line flattening for attacker-shapable values (7DTD_CONNECT): see
# scripts/log_sanitize.sh; same contract as LogText.SanitizeForLog.
source "$SCRIPT_DIR/log_sanitize.sh"

GAME="${GAME:-$HOME/.local/share/Steam/steamapps/common/7 Days To Die}"
STEAM_APPID="${STEAM_APPID:-251570}"
# Prefer the Steam-built Proton runtimes if present; fall back to steam launch.
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
EXTRA_ARGS=(-skipintro -SkipNewsScreen=true -disablenativeinput)

# Which graphics API the client forces. d3d11 stays the default because that is
# what this game ships with on Windows and through Proton, and changing it would
# change what every existing run measures.
#
# It is a variable rather than a constant because a hardcoded -force-d3d11
# cannot be overridden by a caller: Unity takes the *first* -force-* argument it
# is given, so appending another does nothing. That made this launcher unable to
# drive a client on OpenGL or Vulkan at all, which is exactly what an asset
# pipeline needs in order to check that a shader renders on more than one
# graphics API. Set GFX_API=vulkan, glcore, d3d11 or d3d12 - or none, to let the
# game choose.
GFX_API="$(lower "$(trim "${GFX_API:-d3d11}")")"
case "$GFX_API" in
  d3d11|d3d12|vulkan|glcore) GFX_ARGS=(-force-"$GFX_API") ;;
  none) GFX_ARGS=() ;;
  *)
    echo "launch_client.sh: GFX_API must be d3d11, d3d12, vulkan, glcore or none (got '$GFX_API')" >&2
    exit 2
    ;;
esac
if [[ -n "$CONNECT" ]]; then
  EXTRA_ARGS+=(-connect="$CONNECT")
fi

# Mute client audio by default (opt-out). env_bool applies the documented
# table ("0", " Off " and "NO" all read as the opt-out; a blank value is not an
# opt-out and keeps the default, mute on) and warns on a token outside it, so
# a typo cannot silently mute a session the operator meant to hear. The alias
# is a second candidate rather than a separate rule: a whitespace-only
# CLIENT_MUTE is not set, and reading it as set left MUTE_CLIENT empty, which
# reads as the opt-out downstream and silenced a client the operator never
# opted out of.
MUTE_CLIENT="$(env_bool \
  "CLIENT_MUTE=${CLIENT_MUTE-}" \
  "SEVEN_DAYS_TO_DIE_CLIENT_MUTE=${SEVEN_DAYS_TO_DIE_CLIENT_MUTE-}" \
  1)"
MUTE_WAIT="$(trim "${CLIENT_MUTE_TIMEOUT:-${SEVEN_DAYS_TO_DIE_CLIENT_MUTE_TIMEOUT:-60}}")"
# Validated here, not only in the helper: the launcher is what announces the
# poll window it starts, and the helper's own guard is for standalone use.
# Same shared check, so a value the launcher announces is one the helper runs.
if ! is_mute_wait "$MUTE_WAIT"; then
  echo "WARN: CLIENT_MUTE_TIMEOUT invalid ('$(sanitize_log_text "$MUTE_WAIT")'); using 60." >&2
  MUTE_WAIT=60
fi

# Optional no-Steam client mode (see ../7dtd-loadgen/docs/STOCK_AUTH.md Option A):
# CLIENT_PLATFORM=local backs up the game's platform.cfg, selects the Local
# platform with EOS crossplay off, and restores the original on exit. The
# stock dedicated accepts Local clients with no ticket (serverplatforms
# includes Local; loadgen bots ride this path), so the real client can join a
# test server without valid Steam auth and without any server-side bypass mod.
LOCAL_PLATFORM=0
# Trim + case-fold before matching so LOCAL/Lan/" local " behave like the
# documented value (same shape as the MUTE_CLIENT opt-out above). An
# unrecognized non-empty value warns instead of silently launching with Steam
# auth: the join would then fail much later with opaque auth errors.
PLATFORM_MODE="$(trim "${CLIENT_PLATFORM:-}")"
case "${PLATFORM_MODE,,}" in
  "") ;;
  1 | local | lan) LOCAL_PLATFORM=1 ;;
  *)
    echo "WARN: launch_client.sh: CLIENT_PLATFORM='$(sanitize_log_text "$PLATFORM_MODE")' is not 1/local/lan; ignoring (Steam client mode)" >&2
    ;;
esac
PLATFORM_CFG="$GAME/platform.cfg"
PLATFORM_BAK="$GAME/platform.cfg.re-localbak"
# Held for this launcher's whole life while it owns the swap: the backup file
# is a single slot, so two launchers sharing the install would each back up
# (and later restore) the other's config, ending with platform.cfg stuck on
# Local and the player's Steam choice lost.
PLATFORM_LOCK="$GAME/platform.cfg.re-local.lock"
PLATFORM_LOCK_FD=""
# Whether this process created the backup, and so may restore it.
PLATFORM_SWAPPED=0

# Takes the exclusive platform.cfg swap lock without blocking. Returns 1 when
# another live launcher holds it. A missing flock degrades to the previous
# unlocked behavior with a warning rather than refusing to launch.
acquire_platform_lock() {
  if ! command -v flock >/dev/null 2>&1; then
    echo "WARN: flock not found; platform.cfg swap is not exclusive across launchers" >&2
    return 0
  fi
  if ! exec {PLATFORM_LOCK_FD}>>"$PLATFORM_LOCK"; then
    echo "WARN: cannot open $PLATFORM_LOCK; platform.cfg swap is not exclusive across launchers" >&2
    PLATFORM_LOCK_FD=""
    return 0
  fi
  if ! flock -n "$PLATFORM_LOCK_FD"; then
    exec {PLATFORM_LOCK_FD}>&-
    PLATFORM_LOCK_FD=""
    return 1
  fi
  return 0
}

swap_local_platform() {
  if ! acquire_platform_lock; then
    # Another live launcher owns the swap (it holds the lock from before its
    # own swap until it exits), so the install is already in Local mode.
    # Swapping again would back up that launcher's Local config and destroy
    # the Steam original on restore.
    echo "Client platform: Local (another launcher holds the swap; no second backup taken)"
    return 0
  fi
  # A previous hard-killed run (SIGKILL cannot be trapped) may have left the
  # config swapped with a backup behind; restore it first so the swap is
  # idempotent and self-healing.
  if [[ -f "$PLATFORM_BAK" ]]; then
    if mv "$PLATFORM_BAK" "$PLATFORM_CFG"; then
      echo "Client platform.cfg restored from a previous interrupted run"
    else
      # Swapping over an unrestorable original would risk losing the user's
      # real platform choice, so refuse the swap instead.
      echo "WARN: could not restore $PLATFORM_CFG from backup; refusing the Local-platform swap" >&2
      return 1
    fi
  fi
  if [[ ! -f "$PLATFORM_CFG" ]]; then
    echo "WARN: $PLATFORM_CFG missing; cannot switch to Local platform" >&2
    return 0
  fi
  # Back up through a temp file in the same directory and rename it into the
  # backup slot. A cp interrupted by a full disk, a signal, or a crash would
  # otherwise leave a truncated backup, and the self-heal above moves that
  # backup over platform.cfg on the next launch: the user's real Steam config
  # would be replaced by half a file, silently. rename is atomic, so the slot
  # holds either the whole original or nothing, and "nothing" takes the
  # refuse-the-swap branch instead of destroying anything.
  local bak_tmp="$PLATFORM_BAK.tmp.$$"
  # Removed first, so a pre-created name is unlinked rather than written
  # through: cp and the redirect below both follow a symlink, and this one is
  # a predictable pid-suffixed path in the game install dir.
  rm -f "$bak_tmp"
  if ! cp "$PLATFORM_CFG" "$bak_tmp"; then
    rm -f "$bak_tmp" 2>/dev/null || true
    echo "WARN: could not back up $PLATFORM_CFG; refusing the Local-platform swap" >&2
    return 1
  fi
  if ! mv "$bak_tmp" "$PLATFORM_BAK"; then
    rm -f "$bak_tmp" 2>/dev/null || true
    echo "WARN: could not write backup $PLATFORM_BAK; refusing the Local-platform swap" >&2
    return 1
  fi
  PLATFORM_SWAPPED=1
  # Through a temp file in the same directory and a rename, for the reason the
  # backup above gives: a bare redirect truncates the live config before a
  # single byte is written, so a crash, a signal, or a full disk in between
  # leaves platform.cfg empty for the client that reads it moments later. The
  # rename puts the Steam config or the Local one in the file, never a partial
  # one, and a swap that cannot be written leaves the original in place.
  local cfg_tmp="$PLATFORM_CFG.tmp.$$"
  # Same reason as the backup temp above: the redirect follows a symlink, so
  # the name is unlinked first rather than written through.
  rm -f "$cfg_tmp"
  if ! printf 'platform=Local\ncrossplatform=None\nserverplatforms=Steam,LAN,Local,\n' >"$cfg_tmp"; then
    rm -f "$cfg_tmp" 2>/dev/null || true
    PLATFORM_SWAPPED=0
    echo "WARN: could not write $PLATFORM_CFG; the Local-platform swap is abandoned (Steam config left in place, backup at $PLATFORM_BAK)" >&2
    return 1
  fi
  if ! mv "$cfg_tmp" "$PLATFORM_CFG"; then
    rm -f "$cfg_tmp" 2>/dev/null || true
    PLATFORM_SWAPPED=0
    echo "WARN: could not install $PLATFORM_CFG; the Local-platform swap is abandoned (backup kept at $PLATFORM_BAK)" >&2
    return 1
  fi
  echo "Client platform: Local (no Steam auth; restored on exit)"
}

restore_platform() {
  # Only the launcher that took the backup may consume it. Restoring a backup
  # another live launcher still owns would put a Local config back under a
  # running client and delete the original it is about to restore.
  if ((PLATFORM_SWAPPED == 1)) && [[ -f "$PLATFORM_BAK" ]]; then
    # Runs from the EXIT/signal traps; a failure cannot be retried there, but
    # it must at least be named (the backup survives, so the next launch's
    # self-heal retries).
    if mv "$PLATFORM_BAK" "$PLATFORM_CFG"; then
      echo "Client platform.cfg restored"
    else
      echo "WARN: platform.cfg restore failed; backup kept at $PLATFORM_BAK" >&2
    fi
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
  if [[ "$MUTE_CLIENT" == "0" ]]; then
    return 0
  fi
  if [[ ! -x "$MUTE_HELPER" ]]; then
    chmod +x "$MUTE_HELPER" 2>/dev/null || true
  fi
  if [[ -x "$MUTE_HELPER" ]]; then
    echo "Client mute: on (opt-out CLIENT_MUTE=0); polling up to ${MUTE_WAIT}s"
    # Background: audio stream appears after Unity init, not at process start.
    # The timeout rides argv ($1); the helper's env fallbacks are only for
    # standalone use, so no duplicate channel here.
    #
    # Job control is enabled for this one job so the helper leads its own
    # process group: stop_mute_poll then signals the group, and the pactl call
    # the helper sits in dies with it. Signalling the shell alone left that
    # call running against an audio server nobody is waiting for any more.
    set -m
    "$MUTE_HELPER" "$MUTE_WAIT" &
    MUTE_PID=$!
    set +m
  else
    echo "WARN: mute helper missing ($MUTE_HELPER); client audio not muted." >&2
  fi
}

# Seconds the mute poller gets to exit on TERM before it is killed. The reap
# below runs on this launcher's exit path (on_exit, and INT/TERM through it),
# so an unbounded wait there would hang the shell: the game exiting would
# never return control, and in Local-platform mode platform.cfg would stay
# swapped for as long as the hang lasted. Overridable so a host whose audio
# stack is slower to unwind is not reported as a wedged helper.
MUTE_POLL_STOP_GRACE_SEC="$(trim "${MUTE_POLL_STOP_GRACE_SEC:-5}")"
if ! [[ "$MUTE_POLL_STOP_GRACE_SEC" =~ ^[0-9]+$ ]] || ((MUTE_POLL_STOP_GRACE_SEC < 1)); then
  echo "WARN: MUTE_POLL_STOP_GRACE_SEC invalid ('$(sanitize_log_text "$MUTE_POLL_STOP_GRACE_SEC")'); using 5." >&2
  MUTE_POLL_STOP_GRACE_SEC=5
fi

# The poller is only useful while the game runs; stop and reap it so it does
# not outlive this script still polling pactl for a dead client.
stop_mute_poll() {
  if [[ -z "$MUTE_PID" ]]; then
    return 0
  fi
  if kill -0 "$MUTE_PID" 2>/dev/null; then
    # Group first (see start_mute_poll), pid second: the group also takes the
    # pactl call the helper is blocked in, and the pid covers a launcher whose
    # job control was unavailable and left the helper in this shell's group.
    kill -TERM -- "-$MUTE_PID" 2>/dev/null || kill -TERM "$MUTE_PID" 2>/dev/null || true
    # A watchdog bounds the reap, because a bare wait has no timeout of its
    # own: a helper that ignores TERM (bash defers an untrapped TERM until the
    # foreground call it is blocked in returns) would hold the launcher open
    # for as long as that call took, and this runs on the exit path, where the
    # game exiting must return control and restore platform.cfg.
    (
      sleep "$MUTE_POLL_STOP_GRACE_SEC"
      echo "WARN: mute helper ignored TERM for ${MUTE_POLL_STOP_GRACE_SEC}s; killing it" >&2
      kill -9 -- "-$MUTE_PID" 2>/dev/null || kill -9 "$MUTE_PID" 2>/dev/null || true
    ) &
    local watchdog_pid=$!
    wait "$MUTE_PID" 2>/dev/null || true
    # The helper is reaped; stop the watchdog before it can report a kill that
    # never happened, and reap it in turn so no job outlives this script.
    kill "$watchdog_pid" 2>/dev/null || true
    wait "$watchdog_pid" 2>/dev/null || true
  else
    wait "$MUTE_PID" 2>/dev/null || true
  fi
  MUTE_PID=""
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

PREFIX_USER="$(resolve_prefix_user "$COMPAT")"
LOGDIR="$COMPAT/pfx/drive_c/users/$PREFIX_USER/AppData/Roaming/7DaysToDie/logs"
mkdir -p "$LOGDIR"
# Same file as WIN_LOGFILE below: LOGFILE is the prefix-side path, WIN_LOGFILE
# the in-guest path handed to -logfile.
LOGFILE="$LOGDIR/output_log_client_7dtd_connect.txt"
WIN_LOGFILE="C:/users/$PREFIX_USER/AppData/Roaming/7DaysToDie/logs/output_log_client_7dtd_connect.txt"

if [[ -n "$PROTON" && -d "$COMPAT" ]]; then
  export STEAM_COMPAT_DATA_PATH="$COMPAT"
  export STEAM_COMPAT_CLIENT_INSTALL_PATH="${STEAM_COMPAT_CLIENT_INSTALL_PATH:-$STEAM_ROOT}"
  # V 3.2 InControl calls XInputGetState at boot; Proton's xinput1_3
  # hard-crashes there on a Steam-free Local client.
  export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:+$WINEDLLOVERRIDES;}xinput1_3.dll=d;xinput1_4.dll=d;xinput9_1_0.dll=d"
  echo "Proton: $PROTON"
  echo "Connect: $(sanitize_log_text "${CONNECT:-"(none; use F1 connect after menu)"}")"
  echo "Log: $LOGFILE"
  cd "$GAME"
  # Cannot mute after exec: run proton, mute in parallel, wait for the game.
  env 7DTD_CONNECT="${CONNECT:-}" "$PROTON" run ./7DaysToDie.exe "${GFX_ARGS[@]}" -nogs -noeac -logfile "$WIN_LOGFILE" "${EXTRA_ARGS[@]}" "$@" &
  game_pid=$!
  GAME_PID="$game_pid"
  start_mute_poll
  launch_status=0
  wait "$game_pid" || launch_status=$?
  exit "$launch_status"
fi

# Fallback: Steam app launch (may still run EAC depending on launcher settings).
# Feature-test like every other external tool in this repo (package.sh): with
# neither Proton nor a steam launcher on PATH, fail here with the fix instead
# of a bare background-job "command not found" and exit 127.
if ! command -v steam >/dev/null 2>&1; then
  echo "ERROR: no usable Proton found and no 'steam' launcher on PATH; install Steam or set PROTON=/path/to/proton" >&2
  exit 1
fi
echo "Proton not found; using steam -applaunch $STEAM_APPID (set UseEAC false in launcher if needed)"
echo "Connect: $(sanitize_log_text "${CONNECT:-"(none)"}")"
# Steam does not reliably pass -connect=; pass the canonical name through
# `env` because bash cannot export a name starting with a digit.
env 7DTD_CONNECT="${CONNECT:-}" steam -applaunch "$STEAM_APPID" -noeac "${GFX_ARGS[@]}" "${EXTRA_ARGS[@]}" "$@" &
steam_pid=$!
start_mute_poll
launch_status=0
wait "$steam_pid" || launch_status=$?
exit "$launch_status"
