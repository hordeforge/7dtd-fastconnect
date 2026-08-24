#!/usr/bin/env bash
# Flatten control characters in values written to lifecycle logs.
#
# Shell-side twin of ConnectTarget.SanitizeForLog: 7DTD_CONNECT / CYCLE are
# attacker-shapable (a clicked steam://run URL chooses -connect= text), and
# join tooling greps these logs for fixed markers ("result=", "===" cycle
# headers); an embedded newline could forge those markers without ever
# connecting. C0 controls and DEL become spaces so one value stays one line.
# Source this file; do not execute it.

sanitize_log_text() {
	printf '%s' "$1" | tr '\0-\37\177' ' '
}
