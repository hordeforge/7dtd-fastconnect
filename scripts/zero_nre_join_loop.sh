#!/usr/bin/env bash
# Autonomous loop: run stock auto-connect join cycles until NRE count is 0
# after "Found own player", or max attempts.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRATCH="${SCRATCH:-/tmp/grok-goal-67089ec46dbc/implementer}"
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
CLIENT_LOG_SRC="$HOME/.local/share/Steam/steamapps/compatdata/251570/pfx/drive_c/users/steamuser/AppData/Roaming/7DaysToDie/logs/output_log_client_zdtd_connect.txt"

log() { printf '[zero_nre] %s\n' "$*" | tee -a "$SCRATCH/zero_nre_loop.log"; }

kill_zdtd() {
  python3 - <<'PY'
import os, signal, pathlib
for p in pathlib.Path('/proc').iterdir():
    if not p.name.isdigit(): continue
    try:
        cmd = (p/'cmdline').read_bytes().split(b'\0')
    except Exception:
        continue
    if cmd and cmd[0] and cmd[0].split(b'/')[-1] == b'zdtd':
        try: os.kill(int(p.name), signal.SIGTERM)
        except ProcessLookupError: pass
PY
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
    if ss -tln | rg -q ":${PORT}\\b"; then
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
  local logf="$1"
  python3 - <<PY
from pathlib import Path
p = Path("$logf")
if not p.exists():
    print(9999)
    raise SystemExit
lines = p.read_text(errors="replace").splitlines()
join_i = None
for i, l in enumerate(lines):
    if "Found own player entity with id" in l:
        join_i = i
if join_i is None:
    print(9998)  # no join
    raise SystemExit
# count NRE lines after join signal
n = sum(1 for l in lines[join_i:] if "NullReferenceException" in l)
print(n)
PY
}

: >"$SCRATCH/zero_nre_loop.log"
log "start max_attempts=$MAX_ATTEMPTS"
start_zdtd

attempt=1
while (( attempt <= MAX_ATTEMPTS )); do
  log "=== attempt $attempt/$MAX_ATTEMPTS ==="
  : >"$CLIENT_LOG_SRC"
  CYCLE="zn$attempt" TIMEOUT_SEC="$TIMEOUT_SEC" START_SERVER=0 PORT="$PORT" HOST="$HOST" \
    SCRATCH="$SCRATCH" bash "$ONE_SHOT" | tee "$SCRATCH/zero_nre-cycle-$attempt.txt" || true
  # Prefer one_shot copy; also raw client log
  LOG_COPY="$SCRATCH/stock-join-zn${attempt}.log"
  if [[ ! -f "$LOG_COPY" ]]; then
    LOG_COPY="$SCRATCH/stock-join-${attempt}.log"
  fi
  # one_shot names stock-join-${CYCLE}.log
  LOG_COPY="$SCRATCH/stock-join-zn${attempt}.log"
  if [[ ! -f "$LOG_COPY" ]]; then
    cp -f "$CLIENT_LOG_SRC" "$LOG_COPY" 2>/dev/null || true
  fi
  NRE=$(count_nre_after_join "$LOG_COPY")
  FOUND=$(rg -c "Found own player entity with id" "$LOG_COPY" 2>/dev/null || echo 0)
  RESULT=$(rg -n "^result=" "$SCRATCH/zero_nre-cycle-$attempt.txt" | tail -1 || true)
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
    FOUND2=$(rg -c "Found own player entity with id" "$LOG2" 2>/dev/null || echo 0)
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
