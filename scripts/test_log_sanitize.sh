#!/usr/bin/env bash
# Gate for scripts/log_sanitize.sh: control characters must not survive into
# lifecycle-log values (marker forging via embedded newlines), while normal
# text passes through byte-identical.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"
source "$ROOT/scripts/log_sanitize.sh"

flat() {
	[[ "$(sanitize_log_text "$1")" == "$2" ]]
}

assert "newline is flattened (result= marker cannot be forged)" \
	flat $'127.0.0.1:1\nresult=joined' '127.0.0.1:1 result=joined'
assert "carriage return is flattened" flat $'a\rb' 'a b'
assert "tab is flattened" flat $'a\tb' 'a b'
assert "ESC and DEL are flattened" flat $'\033[2J\177x' ' [2J x'
assert "plain target is unchanged" flat '127.0.0.1:27025' '127.0.0.1:27025'
assert "steam URL form is unchanged" flat 'steam://connect/10.0.0.9:26900' 'steam://connect/10.0.0.9:26900'
assert "empty value stays empty" flat '' ''

# The lifecycle scripts that persist attacker-shapable values must route them
# through the helper; a new raw echo would reintroduce marker forging.
for f in one_shot_join.sh launch_client.sh; do
	assert "$f sources log_sanitize.sh" grep -q 'log_sanitize.sh' "$ROOT/scripts/$f"
	assert "$f uses sanitize_log_text" grep -q 'sanitize_log_text' "$ROOT/scripts/$f"
done

finish
