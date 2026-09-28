#!/usr/bin/env bash
# Autonomous loop: run stock auto-connect join cycles until NRE count is 0
# after "Found own player", or max attempts.
set -euo pipefail

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  cat <<'EOF'
Usage: zero_nre_join_loop.sh

Drive one_shot_join.sh cycles against its own zdtd server until a joined
cycle shows zero NREs after "Found own player" twice in a row, or
MAX_ATTEMPTS is reached. Progress goes to stdout and SCRATCH logs.

Exit status: 0 confirmed zero-NRE join | 1 budget exhausted or the
             zdtd server failed to listen | 2 usage error or ZDTD_BIN missing.

Key env vars:
  HOST           join target for each cycle (default 127.0.0.1)
  PORT           zdtd listen port (default 27025)
  MAX_ATTEMPTS   cycle budget (default 6)
  TIMEOUT_SEC    per-cycle join wait in seconds (default 90)
  ZDTD_BIN       zdtd binary (default ../zdtd-server/zig-out/bin/zdtd)
  SCRATCH        artifact dir (default ~/.cache/7dtd-fastconnect)
  WORLD_DIR      world the started server loads (default ../zdtd-server/worlds/zdtd_goal)
  MAP_DIR / GAME_DIR
                 map and dedicated-server install the started server uses
  GAME / COMPAT / STEAM_ROOT / STEAM_APPID
                 client install and Proton prefix, resolved exactly as
                 launch_client.sh does; each cycle polls the client log in
                 that prefix
Numeric knobs fall back to their default with a warning.
EOF
  exit 0
fi

# No positional arguments (every knob is an env var): a mistyped flag must not
# be swallowed into a loop that then spawns servers and clients.
if (( $# != 0 )); then
	echo "usage: ${0##*/} (takes no arguments; got $#)" >&2
	exit 2
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/proton_paths.sh"
# Shared value checks (is_tcp_port, is_bounded_uint): see
# scripts/config_validate.sh.
source "$ROOT/scripts/config_validate.sh"
# Log-line flattening for operator-supplied values (PORT, the numeric knobs):
# see scripts/log_sanitize.sh. Sourced before the value checks below so their
# rejection lines carry a flattened value, not the raw one.
source "$ROOT/scripts/log_sanitize.sh"
SCRATCH="${SCRATCH:-${XDG_CACHE_HOME:-$HOME/.cache}/7dtd-fastconnect}"
mkdir -p "$SCRATCH"
# Bound accumulation: zero_nre creates per-attempt logs that would grow without
# limit if the harness is run repeatedly (e.g. CI). Prune old cycles.
find "$SCRATCH" -maxdepth 1 -type f \( -name 'stock-join-zn*.log' -o -name 'zero_nre-cycle-*.txt' -o -name 'zero_nre_summary.txt' \) -mtime +3 -delete 2>/dev/null || true
PORT="${PORT:-27025}"
HOST="${HOST:-127.0.0.1}"
MAX_ATTEMPTS="${MAX_ATTEMPTS:-6}"
TIMEOUT_SEC="${TIMEOUT_SEC:-90}"
# Default root of the sibling zdtd checkout; empty when it is not checked
# out here. Fail at the point of use (start_zdtd's listen wait), not here:
# a failing command substitution in a default aborts the script under set -e.
ZDTD_ROOT="$(cd "$ROOT/../zdtd-server" 2>/dev/null && pwd || true)"
ZDTD_BIN="${ZDTD_BIN:-$ZDTD_ROOT/zig-out/bin/zdtd}"
MAP_DIR="${MAP_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/Data/Worlds/Navezgane}"
GAME_DIR="${GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
WORLD_DIR="${WORLD_DIR:-$ZDTD_ROOT/worlds/zdtd_goal}"
ONE_SHOT="$ROOT/scripts/one_shot_join.sh"
STEAM_APPID="${STEAM_APPID:-251570}"
STEAM_ROOT="${STEAM_ROOT:-$HOME/.local/share/Steam}"
# Same prefix resolution as launch_client.sh / one_shot_join.sh: the launcher
# and the one-shot cycle both derive the client log path from GAME's own
# Steam library, so a second-disk install must not fall back to the default
# prefix here or every attempt would read an empty log.
if [[ -z "${CLIENT_LOG_SRC:-}" ]]; then
  CLIENT_GAME="${GAME:-$HOME/.local/share/Steam/steamapps/common/7 Days To Die}"
  COMPAT="$(resolve_compat "$CLIENT_GAME" "$STEAM_APPID" "$STEAM_ROOT" "${COMPAT:-}")"
  CLIENT_LOG_SRC="$COMPAT/pfx/drive_c/users/$(resolve_prefix_user "$COMPAT")/AppData/Roaming/7DaysToDie/logs/output_log_client_7dtd_connect.txt"
fi
# Same guard as one_shot_join.sh: on a fresh prefix (first run, custom COMPAT,
# second-disk library) the guest logs dir does not exist yet, and the per-cycle
# truncation below would abort under set -e before any attempt starts.
mkdir -p "$(dirname "$CLIENT_LOG_SRC")"

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

# PID of the server this run started, empty when none is owned. Ownership is
# by pid, not by process name: ZDTD_BIN is a documented override, and a
# binary whose argv[0] basename is not exactly "zdtd" (a renamed or
# out-of-tree build) is invisible to the pkill sweep, so a name-only stop
# strands this run's server ticking its world at 20 Hz and holding PORT, and
# the next run then waits on the stale listener instead of its own.
server_pid=""

stop_owned_server() {
  [[ -n "$server_pid" ]] || return 0
  if kill -0 "$server_pid" 2>/dev/null; then
    kill -TERM "$server_pid" 2>/dev/null || true
    sleep 1
    kill -KILL "$server_pid" 2>/dev/null || true
  fi
  # Reap: the server is this shell's child, so a killed one that is never
  # waited for lingers as a zombie for the rest of the run.
  wait "$server_pid" 2>/dev/null || true
  server_pid=""
}

# Runs on every exit path (PASS/FAIL/set -e abort): a server started by this
# run is stopped by pid, and any other zdtd (a previous run, a manual launch)
# is swept by name.
stop_zdtd() {
  if pgrep -x zdtd >/dev/null 2>&1; then
    # || true: this runs in the EXIT trap, and a failed log write must not
    # abort (set -e) before the server below is actually stopped.
    log "stopping zdtd server" || true
    kill_zdtd
    pkill -KILL -x zdtd 2>/dev/null || true
  fi
  stop_owned_server
}

start_zdtd() {
  # Stop whatever a previous call or an earlier run left behind, by pid and by
  # name, so this run starts against a free PORT.
  stop_zdtd
  mkdir -p "$WORLD_DIR"
  : >"$SCRATCH/zdtd-server-zero-nre.log"
  # exec: the subshell becomes the server, so the pid recorded below is the
  # server itself on every run rather than a shell that may or may not have
  # been collapsed by the exec optimization.
  (cd "$(dirname "$ZDTD_BIN")/.." && exec stdbuf -oL -eL "$ZDTD_BIN" \
    --port "$PORT" \
    --world "$WORLD_DIR" \
    --map "$MAP_DIR" \
    --game-dir "$GAME_DIR" \
    --world-name Navezgane \
    >"$SCRATCH/zdtd-server-zero-nre.log" 2>&1) &
  server_pid=$!
  for _ in $(seq 1 40); do
    # The listener must be this run's server. A port left held by an earlier
    # server matches the same line, while this run's server has died on the
    # bind failure, and every attempt below would then be scored against a
    # server that is not running. Liveness is checked first, and the probe
    # reads ss into a variable: an `ss | grep -q` pipeline under pipefail
    # reports a live listener as absent when grep's early exit SIGPIPEs a
    # still-writing ss.
    if ! kill -0 "$server_pid" 2>/dev/null; then
      log "server pid $server_pid exited before listening on $PORT"
      tail -30 "$SCRATCH/zdtd-server-zero-nre.log" || true
      return 1
    fi
    listeners="$(ss -tln 2>/dev/null || true)"
    if grep -Eq ":${PORT}\\b" <<<"$listeners"; then
      log "server up on $PORT (pid $server_pid)"
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
# The per-attempt client-log copies are the evidence every verdict below is
# read from, and the fallback that fills a missing one writes to the same path
# one_shot_join.sh writes. A copy left behind by an earlier run of this loop
# is therefore indistinguishable from one this run produced, so an attempt
# whose own copy failed was scored against a previous run's join and could
# report SUCCESS for a cycle that never happened. Removing them up front makes
# a rerun start from the same empty set the first run did; a missing copy then
# means "no evidence", which is what the 9998 below says it means.
rm -f "$SCRATCH"/stock-join-zn*.log
# zero_nre_summary.txt appends per attempt across runs, so its mtime never
# goes stale and the -mtime prune above can never fire on it. Trim to the
# newest lines once per run instead; per-run growth is a handful of lines.
SUMMARY="$SCRATCH/zero_nre_summary.txt"
if [[ -f "$SUMMARY" ]] && (( $(wc -l <"$SUMMARY") > 400 )); then
  tail -n 200 "$SUMMARY" >"$SCRATCH/.zero_nre_summary.tmp" \
    && mv "$SCRATCH/.zero_nre_summary.tmp" "$SUMMARY"
fi
log "start max_attempts=$MAX_ATTEMPTS"

# Validate before start_zdtd: a bad value would abort after the server is up.
# PORT lands in --port argv and an ERE (":${PORT}\b"), so it must be a real
# TCP port; TIMEOUT_SEC/MAX_ATTEMPTS get the numeric guard below.
if ! is_tcp_port "$PORT"; then
  log "WARN: PORT invalid ('$(sanitize_log_text "$PORT")'); using 27025"
  PORT=27025
fi
if ! is_bounded_uint "$MAX_ATTEMPTS"; then
  log "WARN: MAX_ATTEMPTS invalid ('$(sanitize_log_text "$MAX_ATTEMPTS")'); using 6"
  MAX_ATTEMPTS=6
fi
if ! is_bounded_uint "$TIMEOUT_SEC"; then
  log "WARN: TIMEOUT_SEC invalid ('$(sanitize_log_text "$TIMEOUT_SEC")'); using 90"
  TIMEOUT_SEC=90
fi

# start_zdtd always spawns its own server, so a missing binary is a setup
# error, not a listen failure: name it before the trap instead of stalling
# through start_zdtd's 10s wait and reporting "server failed to listen".
if [[ ! -x "$ZDTD_BIN" ]]; then
  log "missing zdtd binary: $ZDTD_BIN"
  exit 2
fi

trap stop_zdtd EXIT

start_zdtd

attempt=1
while (( attempt <= MAX_ATTEMPTS )); do
  log "=== attempt $attempt/$MAX_ATTEMPTS ==="
  : >"$CLIENT_LOG_SRC"
  # Per-attempt evidence logs are kept for 3 days, and one_shot's own copy is
  # allowed to fail: a copy left behind by an earlier run would satisfy the
  # existence test below and be scored as this attempt's evidence, turning a
  # lost artifact into a PASS. Clear them with the client log.
  rm -f "$SCRATCH/stock-join-zn${attempt}.log" "$SCRATCH/stock-join-znok2.log"
  one_shot_rc=0
  CYCLE="zn$attempt" TIMEOUT_SEC="$TIMEOUT_SEC" START_SERVER=0 PORT="$PORT" HOST="$HOST" \
    SCRATCH="$SCRATCH" bash "$ONE_SHOT" 2>&1 | tee "$SCRATCH/zero_nre-cycle-$attempt.txt" || one_shot_rc=$?
  one_shot_rc="${one_shot_rc:-0}"
  # A cycle that died before writing its own result= line (missing server
  # binary, exec failure) leaves the summary entry without a result field, so
  # it reads like a truncated write rather than a cycle that never ran.
  if (( one_shot_rc != 0 )) && ! grep -q "^result=" "$SCRATCH/zero_nre-cycle-$attempt.txt"; then
    echo "result=one_shot_exit_$one_shot_rc" | tee -a "$SCRATCH/zero_nre-cycle-$attempt.txt" >/dev/null
    log "WARN: one_shot_join.sh exited $one_shot_rc for attempt $attempt without a result line"
  fi
  # one_shot names stock-join-${CYCLE}.log
  LOG_COPY="$SCRATCH/stock-join-zn${attempt}.log"
  if [[ ! -f "$LOG_COPY" ]]; then
    cp -f "$CLIENT_LOG_SRC" "$LOG_COPY" 2>/dev/null || true
  fi
  if [[ ! -f "$LOG_COPY" ]]; then
    # Without the log, found=0/nre=9998 below mean "no evidence", which must
    # not be mistaken for a join attempt that cleanly failed.
    log "WARN: no client log for attempt $attempt (one_shot copy and $CLIENT_LOG_SRC both unavailable)"
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
    if [[ ! -f "$LOG2" ]]; then
      # Same stale-evidence trap as the per-attempt log above, and the confirm
      # verdict is what writes the PASS file, so a lost artifact here must not
      # be scored as a clean confirmation.
      log "WARN: no client log for the confirmation cycle (one_shot copy and $CLIENT_LOG_SRC both unavailable)"
    fi
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
