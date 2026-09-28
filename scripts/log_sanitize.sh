#!/usr/bin/env bash
# Flatten control and invisible-format characters in values written to
# lifecycle logs.
#
# Shell-side twin of ConnectTarget.SanitizeForLog: 7DTD_CONNECT / CYCLE are
# attacker-shapable (a clicked steam://run URL chooses -connect= text), and
# join tooling greps these logs for fixed markers ("result=", "===" cycle
# headers); an embedded newline could forge those markers without ever
# connecting. C0 controls and DEL become spaces so one value stays one line.
#
# The Unicode format characters (bidi overrides and embeddings, LRM/RLM, the
# zero-width joiners, the BOM) are invisible on a terminal but present in the
# bytes, so a reader sees a different string than grep does. They are dropped
# rather than blanked: each is invisible in every rendering, and a byte-range
# match on their UTF-8 encoding would also swallow the 0xC2 lead byte of
# ordinary accented text. They are listed as $'\uXXXX' escapes because a
# literal in this file would be invisible to the next editor and unreviewable.
# Source this file; do not execute it.

readonly LOG_INVISIBLE_FORMAT_CHARS=$'\u200b\u200c\u200d\u200e\u200f\u202a\u202b\u202c\u202d\u202e\u2060\u2061\u2062\u2063\u2064\u2066\u2067\u2068\u2069\ufeff'

sanitize_log_text() {
	local text="$1" i c
	for ((i = 0; i < ${#LOG_INVISIBLE_FORMAT_CHARS}; i++)); do
		c="${LOG_INVISIBLE_FORMAT_CHARS:i:1}"
		text="${text//"$c"/}"
	done
	printf '%s' "$text" | tr '\000-\037\177' ' '
}
