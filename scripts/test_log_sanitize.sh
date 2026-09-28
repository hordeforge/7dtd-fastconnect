#!/usr/bin/env bash
# Gate for scripts/log_sanitize.sh: line-breaking characters must not survive
# into lifecycle-log values (marker forging via an embedded newline, a C1 NEL,
# or a Unicode separator), and neither may the invisible Unicode format
# characters a terminal renders as nothing, while normal and non-ASCII text
# passes through byte-identical.
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
# The rest of general category Cf, including the blocks above the BMP that a
# category query on the Mono the game ships on does not know about. The tag
# block is the invisible-tag spoofing vector and the Egyptian controls are
# the Trojan Source ones; a C# category query would let both through here.
assert "soft hyphen is dropped" flat $'zd\u00ADtd.lan' 'zdtd.lan'
assert "Arabic letter mark is dropped" flat $'a\u061Cb' 'ab'
assert "interlinear annotation is dropped" flat $'a\uFFF9b\uFFFBc' 'abc'
assert "tag character is dropped" flat $'a\U000E0061b' 'ab'
assert "Egyptian format control is dropped" flat $'a\U00013430b' 'ab'
assert "shorthand format control is dropped" flat $'a\U0001BCA0b' 'ab'
# A tag run at the tail of a value is the shape the spoofing uses: nothing
# renders, and the value reads as the shorter plain one.
assert "trailing tag run is dropped" flat $'player\U000E007F\U000E007E' 'player'
assert "steam URL form is unchanged" flat 'steam://connect/10.0.0.9:26900' 'steam://connect/10.0.0.9:26900'
# Accented text must survive byte-for-byte: a naive UTF-8 lead-byte range
# (0xC2 0x80-0x9f) would eat the 0xC2 of characters like U+00E9.
assert "accented host passes through intact" flat $'caf\u00e9.lan:27025' $'caf\u00e9.lan:27025'
assert "empty value stays empty" flat '' ''

# The C1 block and the Unicode separators are two-byte UTF-8 sequences that a
# byte-range tr would either miss or take out of every other multi-byte
# character; a log reader breaks the line on them even though grep does not,
# so they are flattened by code point here and by char.IsControl + the same
# two separators in LogText.SanitizeForLog.
assert "C1 NEL is flattened" flat $'a\302\205result=joined' 'a result=joined'
assert "C1 DEL is flattened" flat $'x\302\237y' 'x y'
assert "U+2028 line separator is flattened" flat $'a\342\200\250result=joined' 'a result=joined'
assert "U+2029 paragraph separator is flattened" flat $'a\342\200\251b' 'a b'
assert "multi-byte text is passed through byte-identical" \
	flat $'zdtd.lan/\303\251\360\237\230\200' $'zdtd.lan/\303\251\360\237\230\200'

# The helper runs in whatever locale its caller has. A $'\uXXXX' table is
# decoded by the shell through that locale, so under LC_ALL=C or POSIX the
# escapes stay literal text, the walk substitutes one ASCII character of the
# escape at a time, and a value carrying one accented character comes out
# mangled with its invisible characters still in it. Both are the defects
# this gate exists to prevent, so they are pinned under a non-UTF-8 locale
# and the result must match the UTF-8 one byte for byte.
sanitize_under_locale() {
	LC_ALL="$1" bash -c '
		source "$1/scripts/log_sanitize.sh"
		printf "%s" "$(sanitize_log_text "$2")"
	' _ "$ROOT" "$2"
}

# The helper runs in whatever locale its caller has. A $'\uXXXX' table is
# decoded by the shell through that locale, so under LC_ALL=C or POSIX the
# escapes stay literal text, the walk substitutes one ASCII character of the
# escape at a time, and a value carrying one accented character comes out
# mangled with its invisible characters still in it. Both are the defects
# this gate exists to prevent, so they are pinned under a non-UTF-8 locale
# and the result must match the UTF-8 one byte for byte.
# Takes the locale, input and expected output; compares as a command so it
# fits assert, which runs its arguments.
flat_under_locale() {
	[[ "$(sanitize_under_locale "$1" "$2")" == "$3" ]]
}

for loc in C POSIX C.UTF-8 en_US.UTF-8; do
	assert "accented value survives under LC_ALL=$loc" \
		flat_under_locale "$loc" $'caf\xc3\xa9.lan:27025' $'caf\xc3\xa9.lan:27025'
	# The corruption took the form of mangling the text and leaving the
	# character the helper exists to remove, so both are asserted.
	assert "bidi override is still dropped under LC_ALL=$loc" \
		flat_under_locale "$loc" $'\xe2\x80\xaenidets\xe2\x80\xac' 'nidets'
	assert "line separator is still flattened under LC_ALL=$loc" \
		flat_under_locale "$loc" $'a\xe2\x80\xa8result=joined' 'a result=joined'
	assert "C1 NEL is still flattened under LC_ALL=$loc" \
		flat_under_locale "$loc" $'a\xc2\x85b' 'a b'
	assert "astral tag character is still dropped under LC_ALL=$loc" \
		flat_under_locale "$loc" $'a\xf3\xa0\x81\xa1b' 'ab'
	assert "astral and CJK text passes through under LC_ALL=$loc" \
		flat_under_locale "$loc" $'\xe4\xb8\xad\xe6\x96\x87\xf0\x9f\x98\x80' $'\xe4\xb8\xad\xe6\x96\x87\xf0\x9f\x98\x80'
done

# The lifecycle scripts that persist attacker-shapable values must route them
# through the helper; a new raw echo would reintroduce marker forging.
for f in one_shot_join.sh launch_client.sh; do
	assert "$f sources log_sanitize.sh" grep -q 'log_sanitize.sh' "$ROOT/scripts/$f"
	assert "$f uses sanitize_log_text" grep -q 'sanitize_log_text' "$ROOT/scripts/$f"
done

finish
