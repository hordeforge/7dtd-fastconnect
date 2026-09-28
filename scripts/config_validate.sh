#!/usr/bin/env bash
# Shared value checks for the lifecycle scripts: the join harnesses' PORT
# knob, the unbounded count knobs the loop harness also reads (TIMEOUT_SEC,
# SETTLE_SEC, MAX_ATTEMPTS), and every boolean env knob (CLIENT_MUTE,
# START_SERVER).
#
# All of them land in something that only breaks later: PORT and the counts in
# a numeric comparison, an ERE, a --port argv or $(( )) arithmetic, a boolean
# in a case that reads the wrong side of the documented table. The checks live
# here so every script enforces the same rule; each caller words its own
# message, and chooses between falling back to a default and calling it a
# usage error.
#
# Source this file; do not execute it.

# Env values arrive with whatever spacing and case the caller's shell had, and
# every documented enum value in this repo is lowercase. Both normalizers are
# shared so each knob accepts the same shape instead of one being stricter by
# accident.
trim() { local v="${1-}"; v="${v#"${v%%[![:space:]]*}"}"; printf '%s' "${v%"${v##*[![:space:]]}"}"; }
lower() { printf '%s' "${1,,}"; }

# Longest digit string that still fits bash's signed 64-bit arithmetic
# (999999999999999999, under 2^63-1). Anything longer wraps rather than
# failing, and a wrapped value is a number the caller never wrote.
readonly MAX_SIGNIFICANT_DIGITS=18

# True for a decimal count the arithmetic downstream can hold: digits only,
# and at most MAX_SIGNIFICANT_DIGITS significant ones. Zero is a legal value
# (SETTLE_SEC defaults to it); the range is each knob's own business.
is_bounded_uint() {
	# C locale for the whole check: under a UTF-8 one, [0-9] also ranges
	# over non-ASCII digits, so ٢٧٠٢٥ passed the digit test, reached
	# $((10#...)) and failed there with a bash diagnostic on stderr instead
	# of being rejected as the typo it is.
	local LC_ALL=C
	[[ "$1" =~ ^[0-9]+$ ]] || return 1
	# The significant digits decide, and they are counted before any
	# arithmetic: bash integers are 64-bit and $((10#...)) wraps rather than
	# failing, so 18446744073709551617 (2^64+1) came out as 1 and a
	# comparison against a small bound accepted it.
	local digits="${1#"${1%%[!0]*}"}"
	# An all-zero value strips to nothing, and it is one significant digit,
	# not none: zero is a legal count (SETTLE_SEC defaults to it).
	[[ -n "$digits" ]] || digits=0
	(( ${#digits} <= MAX_SIGNIFICANT_DIGITS ))
}

# True for 1..65535, the same range the client enforces on 7DTD_CONNECT
# (ConnectTarget.TryParse). A value outside it is not a port the client can
# join: --port would be refused and the listener probe (":${PORT}\b") could
# never match, so the run would fail much later as a listen or join timeout
# instead of naming the bad value. Leading zeros are accepted as decimal.
is_tcp_port() {
	local LC_ALL=C
	[[ "$1" =~ ^[0-9]+$ ]] || return 1
	# Five significant digits is the top of the port range, rejected on the
	# string so the length of PORT never reaches the arithmetic.
	local digits="${1#"${1%%[!0]*}"}"
	[[ -n "$digits" && ${#digits} -le 5 ]] || return 1
	local n=$((10#$digits))
	((n >= 1 && n <= 65535))
}

# Read one boolean knob. Usage: env_bool "NAME=value" ["ALIAS=value" ...] DEFAULT
#
# Prints 0 or 1. The first spec whose value trims to non-empty wins, so an
# empty primary falls through to its alias and not to the default; all blank
# prints the default, which is how a blank value keeps the documented
# default rather than reading as an opt-out.
#
# Tokens and the unknown-value rule are the shell twin of the mod's
# EnvFlags (1/true/yes/on opt in, 0/false/no/off opt out, anything else is
# read as on with a warning naming the variable and the value). Without that
# warning a typo in a knob is indistinguishable from a deliberate setting,
# and the same table has to answer for the mod's flags and the scripts'.
env_bool() {
	local -a specs=("$@")
	local default="${specs[${#specs[@]}-1]}" spec name value
	local i
	for ((i = 0; i < ${#specs[@]} - 1; i++)); do
		spec="${specs[i]}"
		name="${spec%%=*}"
		value="$(trim "${spec#*=}")"
		[[ -n "$value" ]] || continue
		case "$(lower "$value")" in
			1 | true | yes | on) printf '1\n'; return 0 ;;
			0 | false | no | off) printf '0\n'; return 0 ;;
		esac
		printf "WARN: %s='%s' is not a documented boolean (1/true/yes/on, or 0/false/no/off to disable); reading it as ON\n" \
			"$name" "$value" >&2
		printf '1\n'
		return 0
	done
	printf '%s\n' "$default"
}
