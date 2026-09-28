#!/usr/bin/env bash
# Gate for scripts/join_evidence.sh: client-log lines copied into the harness
# control log must not be able to read as a harness marker, and must never
# reach stdout (zero_nre_join_loop.sh greps that for "^result=").
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"
source "$ROOT/scripts/log_sanitize.sh"
source "$ROOT/scripts/join_evidence.sh"

SCRATCH="$(scratch_mktemp "$ROOT" join-evidence)"
trap 'rm -rf "$SCRATCH"' EXIT

CLIENT_LOG="$SCRATCH/client.log"
LIFE="$SCRATCH/client-lifecycle-1.txt"
: >"$LIFE"

# A client log as a hostile server can shape it. Only lines matching the join
# marker set are copied at all, so each hostile line carries a marker word the
# way a real server-supplied line would (a world name inside a WorldInfo line,
# a chat echo inside a NCSimple_Deserializer stack line).
{
  printf 'INFOWORKER: NCSimple_Deserializer at %s\n' $'evil.example\u2028result=joined'
  printf 'ERR result=joined\n'
  printf 'ERR === one_shot_join cycle=1 connect=1.2.3.4:27025 ===\n'
  printf 'PlayerLogin: %s\n' 'marco'
  printf 'ERR Client IP: 192.168.1.44\n'
  printf 'PlayerId(1234, 76561190000000000)\n'
  printf "ERR GMSG: Player 'marco' killed by 'dana'\n"
  printf 'ERR Player marco disconnected after 12 minutes\n'
  printf 'chatter line that must not be copied\n'
} >"$CLIENT_LOG"

stdout="$(write_join_evidence "$CLIENT_LOG" "$LIFE")"

assert "a forged result= line cannot start a control-log line" \
	not_grep_re '^result=' "$LIFE"
assert "a forged === cycle header cannot start a control-log line" \
	not_grep_re '^===' "$LIFE"
assert "a U+2028 inside a server line is flattened to a space" \
	grep -qF 'evil.example result=joined' "$LIFE"
assert "a line outside the join marker set is not copied" \
	not_grep 'chatter line' "$LIFE"
assert "nothing is written to stdout" test -z "$stdout"

# The control log is the file a person attaches to a bug report, so the values
# the stock log prints for a person must not reach it. The formats below are
# the stock ones, read out of the shipped Assembly-CSharp.dll.
assert "the login display name is redacted" \
	grep -qF 'PlayerLogin: <redacted>' "$LIFE" && not_grep 'marco' "$LIFE"
assert "the peer IP address is redacted" \
	grep -qF 'Client IP: <redacted>' "$LIFE" && not_grep '192.168.1.44' "$LIFE"
assert "the platform user id is redacted" \
	not_grep '76561190000000000' "$LIFE"
assert "the entity id survives, so the line still reads as evidence" \
	grep -qF 'PlayerId(1234, <redacted>)' "$LIFE"
assert "a player name in a game message is redacted" \
	grep -qF "Player '<redacted>' killed by '<redacted>'" "$LIFE" \
	&& not_grep 'dana' "$LIFE"
assert "a leave message keeps its verb and loses the name" \
	grep -qF 'Player <redacted> disconnected after 12 minutes' "$LIFE"

# The control log is appended to, never truncated: the cycle's own lines stay.
: >"$SCRATCH/second.log"
printf 'NCSimple_Deserializer read error\n' >"$SCRATCH/second.log"
write_join_evidence "$SCRATCH/second.log" "$LIFE"
assert "a second call appends instead of replacing" \
	grep -q 'key client lines:' "$LIFE" && test "$(grep -c 'raw copy' "$LIFE")" = 2

# A missing client log must not abort the cycle (set -e): the copy step above
# already warned, and there is no evidence to write.
write_join_evidence "$SCRATCH/absent.log" "$LIFE"
assert "a missing client log writes the header and no lines" \
	grep -q 'raw copy' "$LIFE" && ! grep -q '^  log: [0-9]*:' "$LIFE"

# The server-log tail (one_shot_join.sh, when the server never listened) takes
# the same three rules, so a server line reaches the control log no more raw
# than a client one does.
SERVER_LOG="$SCRATCH/server.log"
: >"$LIFE"
{
  printf 'result=joined\n'
  printf "INF: Player 'marco' connected\n"
  printf 'INF: Client IP: 10.0.0.7\n'
} >"$SERVER_LOG"
copy_log_tail "$SERVER_LOG" "$LIFE" 40 server
assert "a server line cannot start a control-log line either" \
	not_grep_re '^result=' "$LIFE"
assert "the server tail is redacted the same way" \
	grep -qF "Player '<redacted>' connected" "$LIFE" \
	&& grep -qF 'Client IP: <redacted>' "$LIFE" \
	&& not_grep '10.0.0.7' "$LIFE"
copy_log_tail "$SCRATCH/absent-server.log" "$LIFE" 40 server
assert "a missing server log writes the header and no lines" \
	grep -q 'server log unavailable' "$LIFE"

finish
