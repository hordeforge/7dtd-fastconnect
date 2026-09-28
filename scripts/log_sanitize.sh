#!/usr/bin/env bash
# Flatten line-breaking and invisible-format characters in values written to
# lifecycle logs.
#
# Shell-side twin of LogText.SanitizeForLog: 7DTD_CONNECT / CYCLE are
# attacker-shapable (a clicked steam://run URL chooses -connect= text), and
# join tooling greps these logs for fixed markers ("result=", "===" cycle
# headers); an embedded line break could forge those markers without ever
# connecting. C0 controls, DEL, the C1 block, and U+2028/U+2029 become spaces
# so one value stays one line. That is the same set the C# twin flattens.
# Every replacement is bash pattern substitution, which works in characters;
# tr would have to match the C1 range and the two separators byte-wise, which
# mangles every other multi-byte character. That also keeps the function free
# of external binaries, so it still works in the stripped-PATH runs the
# degradation tests use (a helper with only bash on PATH must not lose the
# flattening that keeps its warning one line).
#
# The Unicode format characters (bidi overrides and embeddings, LRM/RLM, the
# zero-width joiners, the BOM) are invisible on a terminal but present in the
# bytes, so a reader sees a different string than grep does. They are dropped
# rather than blanked: each is invisible in every rendering, so a space left
# behind would only misalign the value. They are listed as $'\uXXXX' escapes
# because a literal in this file would be invisible to the next editor and
# unreviewable.
#
# Source this file; do not execute it.

readonly LOG_INVISIBLE_FORMAT_CHARS=$'\u200b\u200c\u200d\u200e\u200f\u202a\u202b\u202c\u202d\u202e\u2060\u2061\u2062\u2063\u2064\u2066\u2067\u2068\u2069\ufeff'

# U+0001 to U+001F and U+007F. U+0000 is absent because a shell variable
# cannot hold it, so no value reaching this function carries one.
readonly LOG_CONTROL_CHARS=$'\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000a\u000b\u000c\u000d\u000e\u000f\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f\u007f'

sanitize_log_text() {
	local text="$1" i c cp hex
	# Each pass is a whole-value pattern substitution, so the value is walked
	# once per character in each table: 54 of them for a value that has
	# nothing to flatten, which is the common case (a sanitized log line is
	# mostly ordinary text). A presence test first skips the substitution
	# when the character is absent, and it is the same match the
	# substitution itself makes, so the two agree in every locale: the guard
	# never changes what the pass would have written, only whether the walk
	# is worth doing.
	for ((i = 0; i < ${#LOG_INVISIBLE_FORMAT_CHARS}; i++)); do
		c="${LOG_INVISIBLE_FORMAT_CHARS:i:1}"
		[[ $text == *"$c"* ]] && text="${text//"$c"/}"
	done
	for ((i = 0; i < ${#LOG_CONTROL_CHARS}; i++)); do
		c="${LOG_CONTROL_CHARS:i:1}"
		[[ $text == *"$c"* ]] && text="${text//"$c"/ }"
	done
	for ((i = 0x80; i <= 0x9f; i++)); do
		printf -v hex '%04x' "$i"
		printf -v cp "\\u$hex"
		[[ $text == *"$cp"* ]] && text="${text//"$cp"/ }"
	done
	for cp in $'\u2028' $'\u2029'; do
		[[ $text == *"$cp"* ]] && text="${text//"$cp"/ }"
	done
	printf '%s' "$text"
}
