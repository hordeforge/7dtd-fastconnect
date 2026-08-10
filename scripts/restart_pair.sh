#!/usr/bin/env bash
# Restart the zdtd server + stock client pair cleanly.
# A client whose server vanished never rejoins on its own; Proton children also
# survive plain pkill. Kill at wineserver level, then relaunch both.
set -euo pipefail

WORLD="${1:?usage: restart_pair.sh <world-dir> [port]}"
PORT="${2:-27025}"
GAME_SRV="${GAME_SRV:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
ZDTD="${ZDTD:-$HOME/Desktop/7dtd/zdtd/zig-out/bin/zdtd}"
LOGDIR="${LOGDIR:-$HOME/.cache/zdtd-scratch}"
SCRIPTDIR="$(cd "$(dirname "$0")" && pwd)"

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

mkdir -p "$WORLD"
nohup "$ZDTD" --port "$PORT" --world "$WORLD" \
  --map "$GAME_SRV/Data/Worlds/Navezgane" \
  --game-dir "$GAME_SRV" --world-name Navezgane --admin-port 8081 \
  > "$LOGDIR/zdtd-server-$(basename "$WORLD").log" 2>&1 &
echo "server pid $!"

for _ in $(seq 1 20); do
  grep -q 'tick=20Hz' "$LOGDIR/zdtd-server-$(basename "$WORLD").log" 2>/dev/null && break
  sleep 1
done

# PLAYTEST / PLAYTEST_SUITE are inherited by Proton → the
# **7dtd-playtest** mod (not connect). Prefer:
#   make -C ../7dtd-playtest playtest-smoke
# for scored exit codes. This script only launches the pair.
ZDTD_CONNECT="127.0.0.1:$PORT" \
PLAYTEST="${PLAYTEST:-}" \
PLAYTEST_SUITE="${PLAYTEST_SUITE:-}" \
  nohup "$SCRIPTDIR/launch_client.sh" \
  > "$LOGDIR/client-launch-$(basename "$WORLD").log" 2>&1 &
echo "client launcher pid $!"
