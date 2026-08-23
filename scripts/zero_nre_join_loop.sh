#!/usr/bin/env bash
# Autonomous loop: run stock auto-connect join cycles until NRE count is 0
# after "Found own player", or max attempts.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRATCH="${SCRATCH:-${XDG_CACHE_HOME:-$HOME/.cache}/7dtd-connect}"
mkdir -p "$SCRATCH"
PORT="${PORT:-27025}"
HOST="${HOST:-127.0.0.1}"
MAX_ATTEMPTS="${MAX_ATTEMPTS:-6}"
TIMEOUT_SEC="${TIMEOUT_SEC:-90}"
ZDTD_BIN="${ZDTD_BIN:-$(cd "$ROOT/../zdtd" && pwd)/zig-out/bin/zdtd}"
MAP_DIR="${MAP_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Navezgane}"
GAME_DIR="${GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
WORLD_DIR="${WORLD_DIR:-$(cd "$ROOT/../zdtd" && pwd)/worlds/zdtd_goal}"
ONE_SHOT="$ROOT/scripts/one_shot_join.sh"
STEAM_ROOT="${STEAM_ROOT:-$HOME/.local/share/Steam}"
CLIENT_LOG_SRC="${CLIENT_LOG_SRC:-$STEAM_ROOT/steamapps/compatdata/251570/pfx/drive_c/users/steamuser/AppData/Roaming/7DaysToDie/logs/output_log_client_7dtd_connect.txt}"

log() { printf '[zero_nre] %s\n' "$*" | tee -a "$SCRATCH/zero_nre_loop.log"; }

# Count matching lines; "0" when the log is missing or has no matches.
count_matches() {
  local n
  n="$(grep -Eac -- "$1" "$2" 2>/dev/null || true)"
  printf '%s\n' "${n:-0}"
}

kill_zdtd() {
  # Exact-name match: same effect as walking /proc for argv[0] basename == zdtd.
  pkill -TERM -x zdtd 2>/dev/null || true
  sleep 1
}

start_zdtd() {
  kill_zdtd
  mkdir -p "$WORLD_DIR"
  : >"$SCRATCH/zdtd-server-zero-nre.log"
  (cd "$(dirname "$ZDTD_BIN")/.." && stdbuf -oL -eL "$ZDTD_BIN" \
    --port "$PORT" \
    --world "$WORLD_DIR" \
    --map "$MAP_DIR" \
    --game-dir "$GAME_DIR" \
    --world-name Navezgane \
    >"$SCRATCH/zdtd-server-zero-nre.log" 2>&1) &
  local i
  for i in $(seq 1 40); do
    if ss -tln | grep -Eq ":${PORT}\\b"; then
      log "server up on $PORT"
      return 0
    fi
    sleep 0.25
  done
  log "server failed to listen"
  tail -30 "$SCRATCH/zdtd-server-zero-nre.log" || true
  return 1
}

count_nre_after_join() {
  local logf="$1" join_line n
  if [[ ! -f "$logf" ]]; then
    echo 9999
    return
  fi
  # Last join marker; NREs are only counted after it.
  join_line="$(grep -En 'Found own player entity with id' "$logf" 2>/dev/null | tail -1 | cut -d: -f1)"
  if [[ -z "$join_line" ]]; then
    echo 9998  # no join
    return
  fi
  n="$(tail -n +"$join_line" "$logf" 2>/dev/null | grep -Ec 'NullReferenceException' || true)"
  echo "${n:-0}"
}

: >"$SCRATCH/zero_nre_loop.log"
log "start max_attempts=$MAX_ATTEMPTS"

# Validate before start_zdtd: a bad value would abort after the server is up.
if [[ ! "$MAX_ATTEMPTS" =~ ^[0-9]+$ ]]; then
  log "WARN: MAX_ATTEMPTS invalid ('$MAX_ATTEMPTS'); using 6"
  MAX_ATTEMPTS=6
fi
if [[ ! "$TIMEOUT_SEC" =~ ^[0-9]+$ ]]; then
  log "WARN: TIMEOUT_SEC invalid ('$TIMEOUT_SEC'); using 90"
  TIMEOUT_SEC=90
fi

start_zdtd

attempt=1
while (( attempt <= MAX_ATTEMPTS )); do
  log "=== attempt $attempt/$MAX_ATTEMPTS ==="
  : >"$CLIENT_LOG_SRC"
  CYCLE="zn$attempt" TIMEOUT_SEC="$TIMEOUT_SEC" START_SERVER=0 PORT="$PORT" HOST="$HOST" \
    SCRATCH="$SCRATCH" bash "$ONE_SHOT" | tee "$SCRATCH/zero_nre-cycle-$attempt.txt" || true
  # one_shot names stock-join-${CYCLE}.log
  LOG_COPY="$SCRATCH/stock-join-zn${attempt}.log"
  if [[ ! -f "$LOG_COPY" ]]; then
    cp -f "$CLIENT_LOG_SRC" "$LOG_COPY" 2>/dev/null || true
  fi
  NRE=$(count_nre_after_join "$LOG_COPY")
  FOUND=$(count_matches "Found own player entity with id" "$LOG_COPY")
  RESULT=$(grep -En "^result=" "$SCRATCH/zero_nre-cycle-$attempt.txt" | tail -1 || true)
  log "result_line=$RESULT found_own=$FOUND nre_after_join=$NRE"
  echo "attempt=$attempt found=$FOUND nre=$NRE $RESULT" >>"$SCRATCH/zero_nre_summary.txt"
  if [[ "$FOUND" != "0" && "$NRE" == "0" ]]; then
    log "SUCCESS zero NRE after join on attempt $attempt"
    # second confirmation cycle
    log "confirmation cycle"
    : >"$CLIENT_LOG_SRC"
    CYCLE="znok2" TIMEOUT_SEC="$TIMEOUT_SEC" START_SERVER=0 PORT="$PORT" HOST="$HOST" \
      SCRATCH="$SCRATCH" bash "$ONE_SHOT" | tee "$SCRATCH/zero_nre-cycle-confirm.txt" || true
    LOG2="$SCRATCH/stock-join-znok2.log"
    NRE2=$(count_nre_after_join "$LOG2")
    FOUND2=$(count_matches "Found own player entity with id" "$LOG2")
    log "confirm found=$FOUND2 nre=$NRE2"
    if [[ "$FOUND2" != "0" && "$NRE2" == "0" ]]; then
      log "CONFIRM SUCCESS"
      echo "PASS zero_nre attempts=$attempt" | tee "$SCRATCH/zero_nre_PASS.txt"
      exit 0
    fi
    log "confirm failed; continue loop"
  fi
  attempt=$((attempt + 1))
done

log "FAIL after $MAX_ATTEMPTS attempts"
echo "FAIL" | tee "$SCRATCH/zero_nre_FAIL.txt"
exit 1
