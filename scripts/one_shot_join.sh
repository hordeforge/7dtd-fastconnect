#!/usr/bin/env bash
# One stock-client auto-connect cycle against a running (or freshly started) zdtd.
# Always terminates the Proton client process for this run before exit.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRATCH="${SCRATCH:-${XDG_CACHE_HOME:-$HOME/.cache}/7dtd-connect}"
mkdir -p "$SCRATCH"

PORT="${PORT:-27025}"
HOST="${HOST:-127.0.0.1}"
# Bash cannot expand/export names starting with a digit, so read the canonical
# 7DTD_CONNECT via printenv.
CONNECT="$(printenv 7DTD_CONNECT 2>/dev/null || true)"
CONNECT="${CONNECT:-$HOST:$PORT}"
TIMEOUT_SEC="${TIMEOUT_SEC:-240}"
# Validate before the client is launched; arithmetic on a bad value would
# otherwise abort mid-cycle with a cryptic error.
if ! [[ "$TIMEOUT_SEC" =~ ^[0-9]+$ ]]; then
  echo "WARN: TIMEOUT_SEC invalid ('$TIMEOUT_SEC'); using 240." >&2
  TIMEOUT_SEC=240
fi
CYCLE="${CYCLE:-1}"
START_SERVER="${START_SERVER:-0}"
# Default root of the sibling zdtd checkout; empty when it is not checked
# out here. A hard failure must wait for the point of use (START_SERVER=1
# validates the binary) so START_SERVER=0 cycles run anywhere.
ZDTD_ROOT="$(cd "$ROOT/../zdtd" 2>/dev/null && pwd || true)"
ZDTD_BIN="${ZDTD_BIN:-$ZDTD_ROOT/zig-out/bin/zdtd}"
GAME_DIR="${GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
MAP_DIR="${MAP_DIR:-$GAME_DIR/Data/Worlds/Navezgane}"
WORLD_DIR="${WORLD_DIR:-$ZDTD_ROOT/worlds/zdtd_goal}"
STEAM_APPID="${STEAM_APPID:-251570}"
STEAM_ROOT="${STEAM_ROOT:-$HOME/.local/share/Steam}"
COMPAT="${COMPAT:-$STEAM_ROOT/steamapps/compatdata/$STEAM_APPID}"
CLIENT_LOG_SRC="$COMPAT/pfx/drive_c/users/steamuser/AppData/Roaming/7DaysToDie/logs/output_log_client_7dtd_connect.txt"
CLIENT_LOG_OUT="$SCRATCH/stock-join-${CYCLE}.log"
SERVER_LOG_OUT="$SCRATCH/zdtd-server-${CYCLE}.log"
LIFE_OUT="$SCRATCH/client-lifecycle-${CYCLE}.txt"
LAUNCH="$ROOT/scripts/launch_client.sh"

server_pid=""
launch_pid=""
client_pgid=""
client_pids_before=()

log() { printf '%s\n' "$*" | tee -a "$LIFE_OUT"; }

# Monotonic seconds since boot (/proc/uptime, CLOCK_BOOTTIME). Bash's SECONDS
# is wall-clock derived: an NTP step or manual correction mid-wait would extend
# or truncate the join timeout (killing a client that was about to spawn).
# Fallback keeps the old behaviour off-Linux.
mono_sec() {
  local up
  if read -r up _ < /proc/uptime 2>/dev/null && [[ "$up" =~ ^[0-9]+([.][0-9]+)?$ ]]; then
    printf '%s\n' "${up%%.*}"
  else
    printf '%s\n' "$SECONDS"
  fi
}

# Join success signal; some checks accept extra partial-progress markers too.
JOINED_RE='Found own player entity with id|PlayerSpawnedInWorld|Spawned in world'
JOIN_SOFT_RE='Found own player entity with id|PlayerSpawnedInWorld|\[7dtd-connect\] .*connected|Created player|Local Player'

list_client_pids() {
  # Match real game process only (not this script's shell line containing the name).
  pgrep -f '[/]7DaysToDie\.exe' 2>/dev/null || true
  pgrep -f 'wine64-preloader.*7DaysToDie' 2>/dev/null || true
}

kill_clients() {
  local pids
  pids="$(list_client_pids)"
  if [[ -z "$pids" ]]; then
    log "kill_clients: no 7DaysToDie.exe"
    return 0
  fi
  log "kill_clients: sending TERM to: $pids"
  # shellcheck disable=SC2086
  kill $pids 2>/dev/null || true
  sleep 2
  pids="$(list_client_pids)"
  if [[ -n "$pids" ]]; then
    log "kill_clients: sending KILL to: $pids"
    # shellcheck disable=SC2086
    kill -9 $pids 2>/dev/null || true
    sleep 1
  fi
  pids="$(list_client_pids)"
  if [[ -n "$pids" ]]; then
    log "kill_clients: STILL ALIVE: $pids"
    return 1
  fi
  log "kill_clients: gone"
  return 0
}

cleanup() {
  local ec=$?
  kill_clients || true
  # The launcher runs detached (setsid) and normally exits when its waited
  # game dies. If the game never appeared (wedged Proton) it would block in
  # wait forever, stacking one orphaned launcher per cycle. TERM lets its own
  # trap restore platform.cfg and stop the mute poller.
  if [[ -n "$launch_pid" ]] && kill -0 "$launch_pid" 2>/dev/null; then
    log "stopping launcher pid=$launch_pid"
    kill "$launch_pid" 2>/dev/null || true
    sleep 1
    kill -9 "$launch_pid" 2>/dev/null || true
  fi
  if [[ -n "$server_pid" ]] && kill -0 "$server_pid" 2>/dev/null; then
    log "stopping server pid=$server_pid"
    kill "$server_pid" 2>/dev/null || true
    sleep 1
    kill -9 "$server_pid" 2>/dev/null || true
  fi
  exit "$ec"
}
trap cleanup EXIT

: >"$LIFE_OUT"
log "=== one_shot_join cycle=$CYCLE connect=$CONNECT timeout=${TIMEOUT_SEC}s ==="
log "before clients: $(list_client_pids | tr '\n' ' ')"

if [[ "$START_SERVER" == "1" ]]; then
  if [[ ! -x "$ZDTD_BIN" ]]; then
    log "missing zdtd binary: $ZDTD_BIN"
    exit 2
  fi
  mkdir -p "$WORLD_DIR"
  : >"$SERVER_LOG_OUT"
  log "starting $ZDTD_BIN --port $PORT"
  "$ZDTD_BIN" \
    --port "$PORT" \
    --world "$WORLD_DIR" \
    --map "$MAP_DIR" \
    --game-dir "$GAME_DIR" \
    --world-name Navezgane \
    >"$SERVER_LOG_OUT" 2>&1 &
  server_pid=$!
  log "server_pid=$server_pid"
  # Wait for TCP GSI port
  for i in $(seq 1 40); do
    if ss -tln | grep -Eq ":${PORT}\\b"; then
      log "server listening on $PORT after ${i}s"
      break
    fi
    sleep 0.5
  done
  if ! ss -tln | grep -Eq ":${PORT}\\b"; then
    log "server failed to listen on $PORT"
    tail -40 "$SERVER_LOG_OUT" | tee -a "$LIFE_OUT" || true
    exit 3
  fi
  # brief settle for LiteNet
  sleep 1
else
  log "START_SERVER=0; expecting existing listener on $PORT"
  if ! ss -tln | grep -Eq ":${PORT}\\b"; then
    log "no listener on $PORT"
    exit 3
  fi
fi

# Truncate client log so we only see this cycle.
mkdir -p "$(dirname "$CLIENT_LOG_SRC")"
: >"$CLIENT_LOG_SRC"

# Kill any leftover client before launch.
kill_clients || true

log "launching client connect=$CONNECT"
# Launch in background; capture proton/game children via pgrep after a beat.
setsid env 7DTD_CONNECT="$CONNECT" "$LAUNCH" >"$SCRATCH/launch-${CYCLE}.log" 2>&1 &
launch_pid=$!
log "launch_pid=$launch_pid"
sleep 3
log "after_launch clients: $(list_client_pids | tr '\n' ' ')"

deadline=$(( $(mono_sec) + TIMEOUT_SEC ))
result="timeout"
while (( $(mono_sec) < deadline )); do
  if [[ -f "$CLIENT_LOG_SRC" ]]; then
    # Strong success first: in-world entity exists. Later package noise must not demote this.
    if grep -Eq "$JOINED_RE" "$CLIENT_LOG_SRC" 2>/dev/null; then
      result="joined"
      # Optional settle for post-join work (control unlock, world settle).
      settle="${SETTLE_SEC:-0}"
      if [[ "$settle" =~ ^[0-9]+$ ]] && (( settle > 0 )); then
        log "joined; settling ${settle}s for post-join (chunks/controls)"
        sleep "$settle"
      fi
      break
    fi
    if grep -Eq 'Kicked from server|NET: LiteNetLib: Disconnect|Failed to connect|connection failed' "$CLIENT_LOG_SRC" 2>/dev/null; then
      # Only treat as fail if we never saw a good join signal
      if ! grep -Eq "$JOIN_SOFT_RE" "$CLIENT_LOG_SRC" 2>/dev/null; then
        result="kick_or_disconnect"
        break
      fi
    fi
    # Strong join bar: PlayerId ProcessPackage created local player, no parse/create failures.
    if grep -Eq 'NET: LiteNetLib: Accepted by server' "$CLIENT_LOG_SRC" 2>/dev/null; then
      if grep -Eq 'EntityFactory CreateEntity: unknown type|NCSimple_Deserializer|Attempted to read past the end of the stream' "$CLIENT_LOG_SRC" 2>/dev/null; then
        # Soft: only fail if we never found our player.
        if ! grep -Eq 'Found own player entity with id' "$CLIENT_LOG_SRC" 2>/dev/null; then
          result="parse_fail"
          break
        fi
      fi
      if grep -Eq "$JOINED_RE" "$CLIENT_LOG_SRC" 2>/dev/null; then
        result="joined"
        break
      fi
      # PlayerId processed without CreateEntity error is partial success (in-world path)
      if grep -Eq 'PlayerId\([0-9]+, [0-9]+\)' "$CLIENT_LOG_SRC" 2>/dev/null \
        && grep -Eq 'Allowed ChunkViewDistance' "$CLIENT_LOG_SRC" 2>/dev/null \
        && ! grep -Eq 'EntityFactory CreateEntity' "$CLIENT_LOG_SRC" 2>/dev/null; then
        sleep 10
        if grep -Eq 'Found own player entity with id|PlayerSpawnedInWorld' "$CLIENT_LOG_SRC" 2>/dev/null; then
          result="joined"
          break
        fi
        if ! grep -Eq 'EntityFactory CreateEntity|NCSimple_Deserializer|Kicked from server' "$CLIENT_LOG_SRC" 2>/dev/null; then
          result="joined"
          break
        fi
      fi
    fi
  fi
  # Client died early
  if ! kill -0 "$launch_pid" 2>/dev/null; then
    if [[ -z "$(list_client_pids)" ]]; then
      # proton launcher exited; check if log has result
      if [[ "$result" == "timeout" ]]; then
        result="client_exit"
      fi
      break
    fi
  fi
  sleep 2
done

cp -f "$CLIENT_LOG_SRC" "$CLIENT_LOG_OUT" 2>/dev/null || true
if [[ -n "$server_pid" && -f "$SERVER_LOG_OUT" ]]; then
  :
elif [[ -f /tmp/zdtd-stock-connect/server.log ]]; then
  cp -f /tmp/zdtd-stock-connect/server.log "$SERVER_LOG_OUT" 2>/dev/null || true
fi

log "result=$result"
log "client log -> $CLIENT_LOG_OUT"
log "key client lines:"
grep -En '7dtd-connect|LiteNetLib: Accepted|NCSimple|PlayerId|PlayerLogin|Spawned|Kicked|WorldInfo|PackageIds|error|ERR' \
  "$CLIENT_LOG_OUT" 2>/dev/null | head -80 | tee -a "$LIFE_OUT" || true

log "after clients before kill: $(list_client_pids | tr '\n' ' ')"
kill_clients
log "after kill clients: $(list_client_pids | tr '\n' ' ')"

case "$result" in
  joined) exit 0 ;;
  *) exit 1 ;;
esac
