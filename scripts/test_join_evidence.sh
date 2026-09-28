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
  printf 'PlayerLogin accepted for entity 42\n'
  printf 'chatter line that must not be copied\n'
} >"$CLIENT_LOG"

stdout="$(write_join_evidence "$CLIENT_LOG" "$LIFE")"

assert "a forged result= line cannot start a control-log line" \
	not_grep_re '^result=' "$LIFE"
assert "a forged === cycle header cannot start a control-log line" \
	not_grep_re '^===' "$LIFE"
assert "a U+2028 inside a server line is flattened to a space" \
	grep -qF 'evil.example result=joined' "$LIFE"
assert "the real evidence line is copied" \
	grep -q 'PlayerLogin accepted for entity 42' "$LIFE"
assert "a line outside the join marker set is not copied" \
	not_grep 'chatter line' "$LIFE"
assert "nothing is written to stdout" test -z "$stdout"

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

finish
