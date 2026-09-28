#!/usr/bin/env bash
# One stock-client auto-connect cycle against a running (or freshly started) zdtd.
# Always terminates the Proton client process for this run before exit.
set -euo pipefail

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  cat <<'EOF'
Usage: one_shot_join.sh

Run one auto-connect join cycle of the stock client against zdtd and exit
0 when the client joined; 1 on any other outcome. This cycle's client
process is always terminated before exit. Artifacts land in SCRATCH.

Exit status: 0 joined | 1 join failed or client exited early
             2 usage error, or START_SERVER=1 but the zdtd binary is missing
             3 server never listened / no listener on PORT

Key env vars:
  HOST / PORT    join target (default 127.0.0.1:27025); a set 7DTD_CONNECT wins
  TIMEOUT_SEC    join wait budget in seconds (default 240)
  SETTLE_SEC     post-join settle window (default 0)
  CYCLE          label for this run's artifact filenames (default 1)
  START_SERVER   1 starts ../zdtd-server/zig-out/bin/zdtd first (default 0)
  SCRATCH        artifact dir (default ~/.cache/7dtd-fastconnect)
  ZDTD_BIN       zdtd binary started when START_SERVER=1
  WORLD_DIR      world the started server loads (default ../zdtd-server/worlds/zdtd_goal)
  MAP_DIR / GAME_DIR
                 map and dedicated-server install the started server uses
  GAME / COMPAT / STEAM_ROOT / STEAM_APPID
                 client install and Proton prefix, resolved exactly as
                 launch_client.sh does; the cycle polls the client log in
                 that prefix
Booleans (START_SERVER) accept 1/true/yes/on and 0/false/no/off; any other
value is read as on with a warning naming the variable. Numeric knobs fall
back to their default with a warning.
EOF
  exit 0
fi

# No positional arguments: every knob is an env var, so a mistyped flag must
# not be swallowed into a cycle that then runs a client.
if (( $# != 0 )); then
  echo "usage: ${0##*/} (takes no arguments; got $#)" >&2
  exit 2
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/proton_paths.sh"
# Shared value checks (is_tcp_port, is_bounded_uint): see
# scripts/config_validate.sh.
source "$ROOT/scripts/config_validate.sh"
# Log-line flattening for attacker-shapable values (7DTD_CONNECT, CYCLE, the
# numeric knobs): see scripts/log_sanitize.sh; same contract as
# LogText.SanitizeForLog. Sourced before the value checks below so their
# rejection lines carry a flattened value, not the raw one.
source "$ROOT/scripts/log_sanitize.sh"
SCRATCH="${SCRATCH:-${XDG_CACHE_HOME:-$HOME/.cache}/7dtd-fastconnect}"
mkdir -p "$SCRATCH"
# Every per-cycle artifact this script writes into SCRATCH. One list, two
# prune rules below, so a new output cannot be added to one and missed by the
# other. Extend it whenever a cycle writes another file into SCRATCH.
CYCLE_ARTIFACTS=('stock-join-*.log' 'launch-*.log' 'client-lifecycle-*.txt' 'zdtd-server-*.log')
# How many files of one pattern survive the count cap. The newest win, by the
# file-time tests bash has built in (-nt/-ot) rather than by an external tool
# printing an mtime: GNU find's -printf and head's `head -n -20` are absent on
# BSD/macOS, and the 2>/dev/null that used to hide the find error turned that
# into a prune that never pruned.
CYCLE_KEEP=20
# Bound disk growth: keep only recent cycles. Defer pruning failures
# (read-only FS) so a full cache never aborts the join.
name_args=()
for pat in "${CYCLE_ARTIFACTS[@]}"; do
  name_args+=(-o -name "$pat")
done
find "$SCRATCH" -maxdepth 1 -type f \( "${name_args[@]:1}" \) -mtime +3 -delete 2>/dev/null || true
# Also cap count: keep at most CYCLE_KEEP newest of each pattern so a tight
# loop with mtime < 3 days cannot fill the disk.
prune_old_artifacts() {
  local keep=() drop=() f i oldest
  # NUL-delimited read: a filename holding whitespace or glob metacharacters
  # must reach rm as one argument, and a newline in one must not split it.
  while IFS= read -r -d '' f; do
    if ((${#keep[@]} < CYCLE_KEEP)); then
      keep+=("$f")
      continue
    fi
    oldest=0
    for ((i = 1; i < ${#keep[@]}; i++)); do
      [[ "${keep[i]}" -ot "${keep[oldest]}" ]] && oldest=$i
    done
    if [[ "$f" -nt "${keep[oldest]}" ]]; then
      drop+=("${keep[oldest]}")
      keep[oldest]="$f"
    else
      drop+=("$f")
    fi
  done < <(find "$SCRATCH" -maxdepth 1 -type f -name "$1" -print0 2>/dev/null)
  for f in ${drop[@]+"${drop[@]}"}; do
    rm -f -- "$f" 2>/dev/null || true
  done
}
for pat in "${CYCLE_ARTIFACTS[@]}"; do
  prune_old_artifacts "$pat"
done

PORT="${PORT:-$DEFAULT_CONNECT_PORT}"
HOST="${HOST:-$DEFAULT_CONNECT_HOST}"
# PORT feeds both --port argv and an ERE (":${PORT}\b"), so it must be a real
# TCP port like TIMEOUT_SEC below: metacharacters would skew the listener
# probe, and a number outside 1..65535 could never match it at all.
if ! is_tcp_port "$PORT"; then
  echo "WARN: PORT invalid ('$(sanitize_log_text "$PORT")'); using $DEFAULT_CONNECT_PORT." >&2
  PORT="$DEFAULT_CONNECT_PORT"
fi
# Bash cannot expand/export names starting with a digit, so read the canonical
# 7DTD_CONNECT via printenv.
CONNECT="$(printenv 7DTD_CONNECT 2>/dev/null || true)"
CONNECT="${CONNECT:-$HOST:$PORT}"
TIMEOUT_SEC="${TIMEOUT_SEC:-240}"
# Validate before the client is launched; arithmetic on a bad value would
# otherwise abort mid-cycle with a cryptic error. is_bounded_uint, not a bare
# regex: a value that wraps in $(( )) becomes a deadline already in the past.
if ! is_bounded_uint "$TIMEOUT_SEC"; then
  echo "WARN: TIMEOUT_SEC invalid ('$(sanitize_log_text "$TIMEOUT_SEC")'); using 240." >&2
  TIMEOUT_SEC=240
fi
# Post-join settle window; same numeric guard as TIMEOUT_SEC so a typo cannot
# silently skip the settle (or sleep on garbage).
SETTLE_SEC="${SETTLE_SEC:-0}"
if ! is_bounded_uint "$SETTLE_SEC"; then
  echo "WARN: SETTLE_SEC invalid ('$(sanitize_log_text "$SETTLE_SEC")'); using 0." >&2
  SETTLE_SEC=0
fi
CYCLE="${CYCLE:-1}"
# CYCLE is interpolated into output filenames (stock-join-${CYCLE}.log,
# client-lifecycle-${CYCLE}.txt) and is attacker-shapable like 7DTD_CONNECT:
# a '/' or '..' would aim this cycle's writes outside SCRATCH. Keep it to
# filename-safe characters and reject a leading dot (".." and hidden files).
if ! [[ "$CYCLE" =~ ^[A-Za-z0-9._-]+$ ]] || [[ "$CYCLE" == .* ]]; then
  echo "WARN: CYCLE invalid ('$(sanitize_log_text "$CYCLE")'); using 1." >&2
  CYCLE=1
fi
# START_SERVER reads the same boolean table as every other knob (and as the
# mod's 7DTD_CONNECT_* flags), so `true`/`yes`/`on` start the server like `1`
# and an empty value keeps the default. A `== "1"` test would instead read
# START_SERVER=true as off and fail later as "no listener on PORT", naming the
# port rather than the knob that was misspelled.
START_SERVER="$(env_bool "START_SERVER=${START_SERVER-}" 0)"
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
CLIENT_LOG_SRC="$COMPAT/pfx/drive_c/users/$(resolve_prefix_user "$COMPAT")/AppData/Roaming/7DaysToDie/logs/output_log_client_7dtd_connect.txt"
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
# scripts/log_sanitize.sh; same contract as LogText.SanitizeForLog.
# Copying client-log evidence into the control log: see
# scripts/join_evidence.sh (needs sanitize_log_text from the line above).
source "$ROOT/scripts/join_evidence.sh"

# Join success signal; some checks accept extra partial-progress markers too.
# The marker set itself is shared with zero_nre_join_loop.sh, which scores this
# cycle's log afterwards: see JOIN_SUCCEEDED_RE in scripts/log_markers.sh.
JOINED_RE="$JOIN_SUCCEEDED_RE"
# Derived from JOINED_RE so the soft set can never drop a strong marker when
# the strong set grows (the kick check below relies on that containment).
JOIN_SOFT_RE="$JOINED_RE|\[7dtd-fastconnect\] .*connected|Created player|Local Player"

list_client_pids() {
  # Match real game process only (not this script's shell line containing the
  # name). One pgrep for both shapes: the poll loop calls this every cycle and
  # each spawn walks /proc.
  pgrep -f '[/]7DaysToDie\.exe|wine64-preloader.*7DaysToDie' 2>/dev/null || true
}

# The client log is append-only for this whole cycle (truncated when the run
# starts, then only ever appended to by the game), so once a marker has
# matched it can never un-match.
# The join poll runs every 2s against a log that grows by megabytes;
# re-grepping every already-decided marker from byte zero each poll is wasted
# I/O competing with the loading client. See scripts/log_markers.sh.
LOG_MARK_FILE="$CLIENT_LOG_SRC"
source "$ROOT/scripts/log_markers.sh"

# Signals a tracked child, and the whole process group when that child leads
# one. The launcher is started with setsid, so its pgid is its pid: stopping
# it on the pid alone left the mute poller and the Proton stack it forked
# running after this cycle returned, and the launcher's own EXIT trap (the one
# that restores platform.cfg) never ran. A child that is not a group leader
# (the zdtd server, a plain background job) is signalled on its own, so a
# negative group id never names an unrelated process group.
signal_owned() {
  local pid="$1" sig="$2" pgid
  [[ -n "$pid" ]] || return 0
  # Liveness first: by the time the KILL step runs, the TERM may already have
  # taken the child and the pid may name a process this run does not own.
  kill -0 "$pid" 2>/dev/null || return 0
  pgid="$(ps -o pgid= -p "$pid" 2>/dev/null | tr -d '[:space:]')" || pgid=""
  if [[ -n "$pgid" && "$pgid" == "$pid" ]]; then
    kill "-$sig" -- "-$pid" 2>/dev/null && return 0
  fi
  kill "-$sig" "$pid" 2>/dev/null || true
}

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
  # trap restore platform.cfg and stop the mute poller, and the group signal
  # reaches the poller and the Proton children even when the launcher takes
  # that trap with it. The log calls carry
  # || true so a failed log write can never abort this trap (set -e) before
  # the processes below are stopped.
  if [[ -n "$launch_pid" ]] && kill -0 "$launch_pid" 2>/dev/null; then
    log "stopping launcher pid=$launch_pid" || true
    signal_owned "$launch_pid" TERM
    sleep 1
    signal_owned "$launch_pid" KILL
    wait "$launch_pid" 2>/dev/null || true
  fi
  if [[ -n "$server_pid" ]] && kill -0 "$server_pid" 2>/dev/null; then
    log "stopping server pid=$server_pid" || true
    signal_owned "$server_pid" TERM
    sleep 1
    signal_owned "$server_pid" KILL
  fi
  exit "$ec"
}
trap cleanup EXIT

: >"$LIFE_OUT"
log "=== one_shot_join cycle=$(sanitize_log_text "$CYCLE") connect=$(sanitize_log_text "$CONNECT") timeout=${TIMEOUT_SEC}s ==="
log "before clients: $(list_client_pids | tr '\n' ' ')"

# ss is read into a variable rather than piped into grep -q: grep exits on the
# first match and closes the pipe, so a still-writing ss takes SIGPIPE and
# pipefail turns that into "no listener" for a port that is listening.
port_listening() {
  local listeners
  listeners="$(ss -tln 2>/dev/null || true)"
  grep -Eq ":${PORT}\\b" <<<"$listeners"
}

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
    if port_listening; then
      log "server listening on $PORT after $(( $(mono_sec) - listen_start ))s"
      break
    fi
    sleep 0.5
  done
  if ! port_listening; then
    log "server failed to listen on $PORT"
    # Same path as the client evidence: flattened, identity values redacted and
    # prefixed, so a server-supplied line cannot read as a harness marker and
    # the control log stays free of the names and addresses the server printed.
    copy_log_tail "$SERVER_LOG_OUT" "$LIFE_OUT" 40 server
    exit 3
  fi
  # brief settle for LiteNet
  sleep 1
else
  log "START_SERVER=0; expecting existing listener on $PORT"
  if ! port_listening; then
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
# Invalidate the marker memo at the write that invalidates it, not only by
# relying on no poll having run yet in this process.
log_marks_reset

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
      if (( SETTLE_SEC > 0 )); then
        log "joined; settling ${SETTLE_SEC}s for post-join (chunks/controls)"
        sleep "$SETTLE_SEC"
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
        && ! log_seen "$JOIN_OWN_PLAYER_RE"; then
        result="parse_fail"
        break
      fi
      # PlayerId processed without CreateEntity error is partial success (in-world path)
      if log_seen 'PlayerId\([0-9]+, [0-9]+\)' && log_seen 'Allowed ChunkViewDistance' \
        && ! log_seen 'EntityFactory CreateEntity'; then
        sleep 10
        # Same join bar as the primary check above; a private copy here would
        # drift when the marker set grows.
        if log_seen "$JOINED_RE"; then
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
  # Client died early: the launcher exited and no game process is left, so
  # the cycle cannot progress any further.
  if ! kill -0 "$launch_pid" 2>/dev/null; then
    if [[ -z "$(list_client_pids)" ]]; then
      result="client_exit"
      break
    fi
  fi
  sleep 2
done

if ! cp -f "$CLIENT_LOG_SRC" "$CLIENT_LOG_OUT" 2>/dev/null; then
  # The copy is the cycle's only retained evidence; losing it must be visible
  # or the lines below read like the log was captured.
  log "WARN: client log copy failed (src=$CLIENT_LOG_SRC out=$CLIENT_LOG_OUT); no client evidence retained"
fi

log "result=$result"
log "client log -> $CLIENT_LOG_OUT"
# Server-influenced client-log lines go into the control log through the
# shared helper, which sanitizes them and prefixes them so none can read as a
# harness marker: see scripts/join_evidence.sh.
write_join_evidence "$CLIENT_LOG_OUT" "$LIFE_OUT"

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
