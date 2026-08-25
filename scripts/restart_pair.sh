#!/usr/bin/env bash
# Restart the zdtd server + stock client pair cleanly.
# A client whose server vanished never rejoins on its own; Proton children also
# survive plain pkill. Kill at wineserver level, then relaunch both.
set -euo pipefail

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  cat <<'EOF'
Usage: restart_pair.sh <world-dir> [port]

Kill any running zdtd server + 7DTD client pair (down to the wine stack),
then relaunch both: zdtd on [port] and the stock client pointed at it via
7DTD_CONNECT. The pair keeps running after this script exits.

Exit status: 0 relaunched, 2 usage error, 1 setup or readiness failure.

Key env vars:
  ZDTD      zdtd binary (default ../zdtd-server/zig-out/bin/zdtd)
  GAME_SRV  dedicated server install dir
  LOGDIR    log dir (default ~/.cache/zdtd-scratch)
EOF
  exit 0
fi

if (( $# < 1 )); then
  echo "usage: $(basename "$0") <world-dir> [port]" >&2
  exit 2
fi
WORLD="$1"
PORT="${2:-27025}"
# PORT goes to --port argv; a non-numeric value is a usage error (exit 2,
# same convention as launch_client.sh GFX_API), not a fallback.
if ! [[ "$PORT" =~ ^[0-9]+$ ]]; then
  echo "ERROR: port must be numeric, got '$PORT'" >&2
  exit 2
fi
GAME_SRV="${GAME_SRV:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
LOGDIR="${LOGDIR:-$HOME/.cache/zdtd-scratch}"
SCRIPTDIR="$(cd "$(dirname "$0")" && pwd)"
# Shared Proton knowledge: prefix derivation and the wine-stack sweep.
source "$SCRIPTDIR/proton_paths.sh"
# Default root of the sibling zdtd checkout (same convention as
# one_shot_join.sh / zero_nre_join_loop.sh); empty when it is not checked out
# next to this repo. Override with ZDTD= when it lives elsewhere; the -x check
# below reports whatever path results.
ZDTD_ROOT="$(cd "$SCRIPTDIR/../zdtd-server" 2>/dev/null && pwd || true)"
ZDTD="${ZDTD:-${ZDTD_ROOT:+$ZDTD_ROOT/zig-out/bin/zdtd}}"

# Validate and prepare BEFORE tearing anything down: a missing binary or
# unwritable dir discovered after the pkill sweep would leave the previous
# server/client pair dead with nothing relaunched.
if [[ ! -x "$ZDTD" ]]; then
  echo "ERROR: zdtd binary not found or not executable: ${ZDTD:-<unset>; set ZDTD=/path/to/zdtd}" >&2
  exit 1
fi
mkdir -p "$WORLD" "$LOGDIR"

# INT/TERM forward to the normal exit path so the EXIT trap runs: a
# backgrounded (nohup) server never sees terminal ^C, so without this a
# Ctrl-C during the startup window strands a freshly spawned zdtd ticking
# its world until some later restart_pair run pkills it (the same
# stranded-server rule one_shot_join.sh and zero_nre_join_loop.sh enforce).
trap 'exit 130' INT
trap 'exit 143' TERM

on_exit() {
  local ec=$?
  # Only a signal aborts mid-startup. The designed failure exits keep their
  # documented behaviour: the listen-failure path stops its own server
  # inline, and a launcher that died immediately leaves the server up ("the
  # server stays up either way"). Success leaves the pair running.
  (( ec >= 128 )) || return 0
  # Same TERM-then-KILL shape as one_shot_join.sh kill_clients / zero_nre's
  # stop_zdtd: a wedged server that ignores TERM must not outlive this run.
  if [[ -n "${server_pid:-}" ]]; then
    kill "$server_pid" 2>/dev/null || true
    sleep 1
    kill -9 "$server_pid" 2>/dev/null || true
  fi
  # TERM lets the launcher's own trap restore platform.cfg and stop its mute
  # poller instead of orphaning them.
  [[ -n "${client_launch_pid:-}" ]] && kill "$client_launch_pid" 2>/dev/null || true
}
trap on_exit EXIT

pkill -f 'zig-out/bin/zdtd' 2>/dev/null || true
# Kill the whole Proton/wine stack, not just the game exe. Leftover
# pressure-vessel containers + wineservers leak threads across relaunches and
# eventually hit RLIMIT_NPROC -> mono "Couldn't create thread" -> the client
# wedges at "Initializing Steam". Sweep them all every restart.
pkill -9 -f '7DaysToDie' 2>/dev/null || true
kill_wine_stack
sleep 3

SERVER_LOG="$LOGDIR/zdtd-server-$(basename "$WORLD").log"
nohup "$ZDTD" --port "$PORT" --world "$WORLD" \
  --map "$GAME_SRV/Data/Worlds/Navezgane" \
  --game-dir "$GAME_SRV" --world-name Navezgane --admin-port 8081 \
  > "$SERVER_LOG" 2>&1 &
server_pid=$!
echo "server pid $server_pid"

for _ in $(seq 1 20); do
  grep -q 'tick=20Hz' "$SERVER_LOG" 2>/dev/null && break
  sleep 1
done

if ! grep -q 'tick=20Hz' "$SERVER_LOG" 2>/dev/null; then
  echo "ERROR: server not ready (no 'tick=20Hz' within 20s); log tail:" >&2
  tail -20 "$SERVER_LOG" >&2 || true
  kill "$server_pid" 2>/dev/null || true
  exit 1
fi

# PLAYTEST / PLAYTEST_SUITE are inherited by Proton → the
# **7dtd-playtest** mod (not connect). Prefer:
#   make -C ../7dtd-playtest playtest-smoke
# for scored exit codes. This script only launches the pair.
env 7DTD_CONNECT="127.0.0.1:$PORT" \
PLAYTEST="${PLAYTEST:-}" \
PLAYTEST_SUITE="${PLAYTEST_SUITE:-}" \
  nohup "$SCRIPTDIR/launch_client.sh" \
  > "$LOGDIR/client-launch-$(basename "$WORLD").log" 2>&1 &
client_launch_pid=$!
echo "client launcher pid $client_launch_pid"
# Same treatment as the server readiness wait: a launcher that dies right away
# (missing game dir, no usable Proton) must not look like a successful
# relaunch. The server stays up either way; only the exit status signals.
sleep 3
if ! kill -0 "$client_launch_pid" 2>/dev/null; then
  echo "ERROR: client launcher exited immediately; see $LOGDIR/client-launch-$(basename "$WORLD").log" >&2
  exit 1
fi
