#!/usr/bin/env bash
# Gate for scripts/log_sanitize.sh: control characters must not survive into
# lifecycle-log values (marker forging via embedded newlines), and neither may
# the invisible Unicode format characters a terminal renders as nothing, while
# normal text passes through byte-identical.
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
# Invisible-format characters (Cf) are not C0 controls, so tr leaves them and a
# terminal renders nothing for them while grep still matches the bytes: a value
# carrying a bidi override would read back differently from what a harness
# greps for. They must be dropped, not flattened to a space.
assert "bidi override is dropped" flat $'\u202egnidets\u202c' 'gnidets'
assert "RLO around a forged marker is dropped" flat $'\u202esult=joined' 'sult=joined'
assert "zero-width space is dropped" flat $'1.2.3.4\u200b:27025' '1.2.3.4:27025'
assert "BOM is dropped" flat $'\ufeff127.0.0.1' '127.0.0.1'
assert "isolated LRI is dropped" flat $'a\u2066b\u2069c' 'abc'
assert "plain target is unchanged" flat '127.0.0.1:27025' '127.0.0.1:27025'
assert "steam URL form is unchanged" flat 'steam://connect/10.0.0.9:26900' 'steam://connect/10.0.0.9:26900'
# Accented text must survive byte-for-byte: a naive UTF-8 lead-byte range
# (0xC2 0x80-0x9f) would eat the 0xC2 of characters like U+00E9.
assert "accented host passes through intact" flat $'caf\u00e9.lan:27025' $'caf\u00e9.lan:27025'
assert "empty value stays empty" flat '' ''

# The lifecycle scripts that persist attacker-shapable values must route them
# through the helper; a new raw echo would reintroduce marker forging.
for f in one_shot_join.sh launch_client.sh; do
	assert "$f sources log_sanitize.sh" grep -q 'log_sanitize.sh' "$ROOT/scripts/$f"
	assert "$f uses sanitize_log_text" grep -q 'sanitize_log_text' "$ROOT/scripts/$f"
done

finish
