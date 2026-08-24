#!/usr/bin/env bash
# One stock-client auto-connect cycle against a running (or freshly started) zdtd.
# Always terminates the Proton client process for this run before exit.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/proton_paths.sh"
SCRATCH="${SCRATCH:-${XDG_CACHE_HOME:-$HOME/.cache}/7dtd-fastconnect}"
mkdir -p "$SCRATCH"
# Bound disk growth: one_shot creates per-cycle logs that would otherwise
# accumulate forever across repeated harness runs. Keep only recent cycles.
# Defer pruning failures (read-only FS) so a full cache never aborts the join.
find "$SCRATCH" -maxdepth 1 -type f \( -name 'stock-join-*.log' -o -name 'launch-*.log' -o -name 'client-lifecycle-*.txt' \) -mtime +3 -delete 2>/dev/null || true
# Also cap count: keep at most 20 newest of each pattern so a tight loop
# with mtime < 3 days cannot fill the disk.
for pat in 'stock-join-*.log' 'launch-*.log' 'client-lifecycle-*.txt'; do
  old="$(find "$SCRATCH" -maxdepth 1 -type f -name "$pat" -printf '%T@ %p\n' 2>/dev/null | sort -n | head -n -20 | cut -d' ' -f2-)" || true
  # Read line-by-line instead of an unquoted expansion: a filename holding
  # whitespace or glob metacharacters must reach rm as one argument.
  while IFS= read -r f; do
    [[ -z "$f" ]] && continue
    rm -f -- "$f" 2>/dev/null || true
  done <<<"$old"
done

PORT="${PORT:-27025}"
HOST="${HOST:-127.0.0.1}"
# PORT feeds both --port argv and an ERE ("::${PORT}\b"), so keep it numeric
# like TIMEOUT_SEC below; metacharacters would silently skew the listener probe.
if ! [[ "$PORT" =~ ^[0-9]+$ ]]; then
  echo "WARN: PORT invalid ('$PORT'); using 27025." >&2
  PORT=27025
fi
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
# CYCLE is interpolated into output filenames (stock-join-${CYCLE}.log,
# client-lifecycle-${CYCLE}.txt) and is attacker-shapable like 7DTD_CONNECT:
# a '/' or '..' would aim this cycle's writes outside SCRATCH. Keep it to
# filename-safe characters and reject a leading dot (".." and hidden files).
if ! [[ "$CYCLE" =~ ^[A-Za-z0-9._-]+$ ]] || [[ "$CYCLE" == .* ]]; then
  echo "WARN: CYCLE invalid ('$CYCLE'); using 1." >&2
  CYCLE=1
fi
START_SERVER="${START_SERVER:-0}"
# Default root of the sibling zdtd checkout; empty when it is not checked
# out here. A hard failure must wait for the point of use (START_SERVER=1
# validates the binary) so START_SERVER=0 cycles run anywhere.
ZDTD_ROOT="$(cd "$ROOT/../zdtd-server" 2>/dev/null && pwd || true)"
ZDTD_BIN="${ZDTD_BIN:-$ZDTD_ROOT/zig-out/bin/zdtd}"
GAME_DIR="${GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
MAP_DIR="${MAP_DIR:-$GAME_DIR/Data/Worlds/Navezgane}"
WORLD_DIR="${WORLD_DIR:-$ZDTD_ROOT/worlds/zdtd_goal}"
STEAM_APPID="${STEAM_APPID:-251570}"
STEAM_ROOT="${STEAM_ROOT:-$HOME/.local/share/Steam}"
# Resolve the client's Proton prefix exactly like launch_client.sh (same GAME
# override, same second-library rule): the launcher truncates and writes the
# client log under its own derived prefix, so polling a differently resolved
# one would watch an empty file and report every join as a timeout on any
# non-default Steam library layout.
CLIENT_GAME="${GAME:-$HOME/.local/share/Steam/steamapps/common/7 Days To Die}"
COMPAT="$(resolve_compat "$CLIENT_GAME" "$STEAM_APPID" "$STEAM_ROOT" "${COMPAT:-}")"
CLIENT_LOG_SRC="$COMPAT/pfx/drive_c/users/steamuser/AppData/Roaming/7DaysToDie/logs/output_log_client_7dtd_connect.txt"
CLIENT_LOG_OUT="$SCRATCH/stock-join-${CYCLE}.log"
SERVER_LOG_OUT="$SCRATCH/zdtd-server-${CYCLE}.log"
LIFE_OUT="$SCRATCH/client-lifecycle-${CYCLE}.txt"
LAUNCH="$ROOT/scripts/launch_client.sh"

server_pid=""
launch_pid=""

log() { printf '%s\n' "$*" | tee -a "$LIFE_OUT"; }

# Monotonic deadline source shared with mute_client_audio.sh: see
# scripts/monotonic_clock.sh for why $SECONDS must not bound these waits.
source "$ROOT/scripts/monotonic_clock.sh"
# Log-line flattening for attacker-shapable values (7DTD_CONNECT): see
# scripts/log_sanitize.sh; same contract as ConnectTarget.SanitizeForLog.
source "$ROOT/scripts/log_sanitize.sh"

# Join success signal; some checks accept extra partial-progress markers too.
JOINED_RE='Found own player entity with id|PlayerSpawnedInWorld|Spawned in world'
JOIN_SOFT_RE='Found own player entity with id|PlayerSpawnedInWorld|\[7dtd-fastconnect\] .*connected|Created player|Local Player'

list_client_pids() {
  # Match real game process only (not this script's shell line containing the
  # name). One pgrep for both shapes: the poll loop calls this every cycle and
  # each spawn walks /proc.
  pgrep -f '[/]7DaysToDie\.exe|wine64-preloader.*7DaysToDie' 2>/dev/null || true
}

# The client log is append-only for this whole cycle (truncated above, then
# written by the game), so once a marker has matched it can never un-match.
# The join poll runs every 2s against a log that grows by megabytes;
# re-grepping every already-decided marker from byte zero each poll is wasted
# I/O competing with the loading client. See scripts/log_markers.sh.
LOG_MARK_FILE="$CLIENT_LOG_SRC"
source "$ROOT/scripts/log_markers.sh"

kill_clients() {
  local pids
  pids="$(list_client_pids)"
  if [[ -z "$pids" ]]; then
    log "kill_clients: no 7DaysToDie.exe"
  else
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
  fi
  # Proton/wine stack outlives the exe: leftover wineservers and
  # pressure-vessel containers leak threads/NPROC across cycles until the
  # client wedges at "Initializing Steam". Sweep them after the exe is gone.
  kill_wine_stack
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
log "=== one_shot_join cycle=$(sanitize_log_text "$CYCLE") connect=$(sanitize_log_text "$CONNECT") timeout=${TIMEOUT_SEC}s ==="
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
  # Wait for TCP GSI port; polls are 0.5s apart, so measure elapsed time on
  # the monotonic clock instead of reporting the poll count as seconds.
  listen_start=$(mono_sec)
  for _ in $(seq 1 40); do
    if ss -tln | grep -Eq ":${PORT}\\b"; then
      log "server listening on $PORT after $(( $(mono_sec) - listen_start ))s"
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

# Kill any leftover client BEFORE truncating the log: a dying client holds
# the log file open at its own write offset, so pre-cycle lines flushed
# during kill_clients would land in (or past) the freshly truncated file and
# cached markers could report a join from a previous cycle's bytes.
kill_clients || true

# Truncate client log so we only see this cycle.
mkdir -p "$(dirname "$CLIENT_LOG_SRC")"
: >"$CLIENT_LOG_SRC"

log "launching client connect=$(sanitize_log_text "$CONNECT")"
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
    if log_seen "$JOINED_RE"; then
      result="joined"
      # Optional settle for post-join work (control unlock, world settle).
      settle="${SETTLE_SEC:-0}"
      if [[ "$settle" =~ ^[0-9]+$ ]] && (( settle > 0 )); then
        log "joined; settling ${settle}s for post-join (chunks/controls)"
        sleep "$settle"
      fi
      break
    fi
    if log_seen 'Kicked from server|NET: LiteNetLib: Disconnect|Failed to connect|connection failed'; then
      # Only treat as fail if we never saw a good join signal
      if ! log_seen "$JOIN_SOFT_RE"; then
        result="kick_or_disconnect"
        break
      fi
    fi
    # Strong join bar: PlayerId ProcessPackage created local player, no parse/create failures.
    if log_seen 'NET: LiteNetLib: Accepted by server'; then
      if log_seen 'EntityFactory CreateEntity: unknown type|NCSimple_Deserializer|Attempted to read past the end of the stream' \
        && ! log_seen 'Found own player entity with id'; then
        result="parse_fail"
        break
      fi
      # PlayerId processed without CreateEntity error is partial success (in-world path)
      if log_seen 'PlayerId\([0-9]+, [0-9]+\)' && log_seen 'Allowed ChunkViewDistance' \
        && ! log_seen 'EntityFactory CreateEntity'; then
        sleep 10
        if log_seen 'Found own player entity with id|PlayerSpawnedInWorld'; then
          result="joined"
          break
        fi
        if ! log_seen 'EntityFactory CreateEntity|NCSimple_Deserializer|Kicked from server'; then
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

log "result=$result"
log "client log -> $CLIENT_LOG_OUT"
log "key client lines:"
grep -En '7dtd-fastconnect|LiteNetLib: Accepted|NCSimple|PlayerId|PlayerLogin|Spawned|Kicked|WorldInfo|PackageIds|[Ee]rror|ERR' \
  "$CLIENT_LOG_OUT" 2>/dev/null | head -80 | tee -a "$LIFE_OUT" || true

log "after clients before kill: $(list_client_pids | tr '\n' ' ')"
# A client that survives SIGKILL makes kill_clients report failure; that must
# not abort here (set -e) before the result-based exit below, or a joined
# cycle would be misreported as failed by the cleanup trap.
kill_clients || true
log "after kill clients: $(list_client_pids | tr '\n' ' ')"

case "$result" in
  joined) exit 0 ;;
  *) exit 1 ;;
esac
