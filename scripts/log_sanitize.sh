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
# The C1 range and the two separators are two-byte UTF-8 sequences, which tr
# cannot match byte-wise without mangling every other multi-byte character,
# so they go through bash pattern substitution, which works in characters.
#
# The Unicode format characters (bidi overrides and embeddings, LRM/RLM, the
# zero-width joiners, the BOM) are invisible on a terminal but present in the
# bytes, so a reader sees a different string than grep does. They are dropped
# rather than blanked: each is invisible in every rendering, and a byte-range
# match on their UTF-8 encoding would also swallow the 0xC2 lead byte of
# ordinary accented text. They are listed as $'\uXXXX' escapes because a
# literal in this file would be invisible to the next editor and unreviewable.
#
# Source this file; do not execute it.

readonly LOG_INVISIBLE_FORMAT_CHARS=$'\u200b\u200c\u200d\u200e\u200f\u202a\u202b\u202c\u202d\u202e\u2060\u2061\u2062\u2063\u2064\u2066\u2067\u2068\u2069\ufeff'

sanitize_log_text() {
	local text="$1" i c cp hex
	for ((i = 0; i < ${#LOG_INVISIBLE_FORMAT_CHARS}; i++)); do
		c="${LOG_INVISIBLE_FORMAT_CHARS:i:1}"
		text="${text//"$c"/}"
	done
	for ((i = 0x80; i <= 0x9f; i++)); do
		printf -v hex '%04x' "$i"
		printf -v cp "\\u$hex"
		text="${text//"$cp"/ }"
	done
	for cp in $'\u2028' $'\u2029'; do
		text="${text//"$cp"/ }"
	done
	printf '%s' "$text" | tr '\000-\037\177' ' '
}
