#!/usr/bin/env bash
# Behavioral test for the shared harness value checks
# (scripts/config_validate.sh), plus the wiring that keeps every join script
# using them: a PORT the client could never join must be rejected where it is
# read, not surface later as a listen or join timeout.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"
source "$ROOT/scripts/config_validate.sh"

valid_port() { is_tcp_port "$1"; }
# assert takes a command whose status decides the check; a bare `! is_tcp_port
# x` would trip set -e's own handling of the failing condition.
rejects() { if is_tcp_port "$1"; then return 1; fi; }

assert "accepts the default port" valid_port 27025
assert "accepts the range bounds" valid_port 1
assert "accepts the top of the range" valid_port 65535
assert "accepts a leading zero as decimal" valid_port 027025
assert "rejects port 0" rejects 0
assert "rejects a port above the range" rejects 65536
assert "rejects non-numeric text" rejects 27a25
assert "rejects an empty value" rejects ''
assert "rejects a value with metacharacters" rejects '27025|ls'

for f in one_shot_join.sh zero_nre_join_loop.sh restart_pair.sh; do
	assert "$f sources the shared checks" grep -q 'config_validate.sh' "$ROOT/scripts/$f"
	assert "$f validates PORT through is_tcp_port" grep -q 'if ! is_tcp_port "$PORT"' "$ROOT/scripts/$f"
done

finish
