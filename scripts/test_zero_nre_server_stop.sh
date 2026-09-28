#!/usr/bin/env bash
# Offline gate for zero_nre_join_loop.sh's server ownership: the loop starts a
# zdtd server and promises to stop it on every exit path. A name-only stop
# (pkill -x zdtd) misses a server whose binary was reached through the
# documented ZDTD_BIN override and is not named "zdtd", stranding a world
# ticking at 20 Hz that also holds PORT for the next run. The started server
# must be tracked by pid, stopped on that pid, and reaped.
# The script itself is never executed here: it starts and stops real servers
# and sweeps processes by name.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

src="$ROOT/scripts/zero_nre_join_loop.sh"

records_server_pid() {
	grep -qE '^\s*server_pid=\$!' "$src"
}

stops_by_pid() {
	grep -q 'kill -TERM "$server_pid"' "$src" && grep -q 'kill -KILL "$server_pid"' "$src"
}

reaps_server() {
	grep -q 'wait "$server_pid"' "$src"
}

# The pid the stop path uses must be cleared after use, or a second call would
# signal a pid the kernel may already have recycled.
clears_owned_pid() {
	grep -qE '^\s*server_pid=""$' "$src"
}

# The name sweep stays: it is what stops a server this run did not start (a
# previous run, a manual launch), which no pid can name.
keeps_name_sweep() {
	grep -q 'pkill -KILL -x zdtd' "$src"
}

# Every start must pass through the stop path, so a relaunch cannot leave the
# previous server holding PORT.
start_stops_first() {
	local start_line stop_call
	start_line="$(grep -n 'start_zdtd()' "$src" | head -1 | cut -d: -f1)"
	[[ -n "$start_line" ]] || return 1
	stop_call="$(grep -n '^\s*stop_zdtd$' "$src" | cut -d: -f1 | head -1)"
	[[ -n "$stop_call" ]] || return 1
	(( stop_call > start_line ))
}

assert "zero_nre_join_loop.sh records the server pid it starts" records_server_pid
assert "zero_nre_join_loop.sh stops its own server by pid" stops_by_pid
assert "zero_nre_join_loop.sh reaps the server it stopped" reaps_server
assert "zero_nre_join_loop.sh clears the owned pid after stopping" clears_owned_pid
assert "zero_nre_join_loop.sh keeps the name sweep for foreign servers" keeps_name_sweep
assert "zero_nre_join_loop.sh stops any previous server before starting" start_stops_first

finish
