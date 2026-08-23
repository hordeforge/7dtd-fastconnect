#!/usr/bin/env bash
# Restart the zdtd server + stock client pair cleanly.
# A client whose server vanished never rejoins on its own; Proton children also
# survive plain pkill. Kill at wineserver level, then relaunch both.
set -euo pipefail

WORLD="${1:?usage: restart_pair.sh <world-dir> [port]}"
PORT="${2:-27025}"
# PORT goes to --port argv; a non-numeric value is a usage error, not a fallback.
if ! [[ "$PORT" =~ ^[0-9]+$ ]]; then
  echo "ERROR: port must be numeric, got '$PORT'" >&2
  exit 1
fi
GAME_SRV="${GAME_SRV:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
LOGDIR="${LOGDIR:-$HOME/.cache/zdtd-scratch}"
SCRIPTDIR="$(cd "$(dirname "$0")" && pwd)"
# Default root of the sibling zdtd checkout (same convention as
# one_shot_join.sh / zero_nre_join_loop.sh); empty when it is not checked out
# next to this repo. Override with ZDTD= when it lives elsewhere; the -x check
# below reports whatever path results.
ZDTD_ROOT="$(cd "$SCRIPTDIR/../zdtd-server-server" 2>/dev/null && pwd || true)"
ZDTD="${ZDTD:-${ZDTD_ROOT:+$ZDTD_ROOT/zig-out/bin/zdtd}}"

pkill -f 'zig-out/bin/zdtd' 2>/dev/null || true
# Kill the whole Proton/wine stack, not just the game exe. Leftover
# pressure-vessel containers + wineservers leak threads across relaunches and
# eventually hit RLIMIT_NPROC -> mono "Couldn't create thread" -> the client
# wedges at "Initializing Steam". Sweep them all every restart.
pkill -9 -f '7DaysToDie' 2>/dev/null || true
pkill -9 -f 'wineserver' 2>/dev/null || true
pkill -9 -f 'pressure-vessel|pv-adverb|pv-bwrap' 2>/dev/null || true
pkill -9 -f 'proton.*7DaysToDie|SteamLaunch.*251570' 2>/dev/null || true
sleep 3

if [[ ! -x "$ZDTD" ]]; then
  echo "ERROR: zdtd binary not found or not executable: ${ZDTD:-<unset>; set ZDTD=/path/to/zdtd}" >&2
  exit 1
fi

mkdir -p "$WORLD" "$LOGDIR"
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
echo "client launcher pid $!"
