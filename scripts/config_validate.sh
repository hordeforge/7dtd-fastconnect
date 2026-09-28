#!/usr/bin/env bash
# Shared value checks for the harness knobs (PORT and friends).
#
# Every one of these values lands in a numeric comparison, an ERE, or a --port
# argv, so a typo has to be caught where the value is read, before the run
# starts a server or a client. The checks live here so the three join scripts
# enforce the same rule; each caller words its own warn/fallback message.
# Source this file; do not execute it.

# True for 1..65535, the same range the client enforces on 7DTD_CONNECT
# (ConnectTarget.TryParse). A value outside it is not a port the client can
# join: --port would be refused and the listener probe (":${PORT}\b") could
# never match, so the run would fail much later as a listen or join timeout
# instead of naming the bad value. Leading zeros are accepted as decimal.
is_tcp_port() {
	[[ "$1" =~ ^[0-9]+$ ]] || return 1
	local n=$((10#$1))
	((n >= 1 && n <= 65535))
}
