#!/usr/bin/env bash
# Shared value checks for the lifecycle scripts: the join harnesses' PORT
# knob, the unbounded count knobs the loop harness also reads (TIMEOUT_SEC,
# SETTLE_SEC, MAX_ATTEMPTS), the client-mute poll window, and every boolean
# env knob (CLIENT_MUTE, START_SERVER).
#
# All of them land in something that only breaks later: PORT and the counts in
# a numeric comparison, an ERE, a --port argv or $(( )) arithmetic, a boolean
# in a case that reads the wrong side of the documented table. The checks live
# here so every script enforces the same rule; each caller words its own
# message, and chooses between falling back to a default and calling it a
# usage error.
#
# Source this file; do not execute it.

# The join target every harness falls back to: the local zdtd pair on the
# stock Connect-to-IP / ServerPort. One owner for the number, because it is
# also the mod's ConnectTarget.DefaultPort, and a harness that carries its own
# literal drifts from the client it is trying to join. The two are compared in
# test_config_validate.sh, so changing one without the other fails a gate
# rather than a run.
# shellcheck disable=SC2034  # read by the harnesses that source this file
DEFAULT_CONNECT_HOST=127.0.0.1
DEFAULT_CONNECT_PORT=27025

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

# Strip leading zeros, the way every decimal knob here is read (10#$v) and the
# way ConnectTarget.TryParse reads a port. Prints the canonical digits; a
# value that is all zeros prints nothing.
canon_uint() { printf '%s' "${1#"${1%%[!0]*}"}"; }

# True for a non-negative decimal integer bash arithmetic can hold exactly.
# The numeric knobs that use this all reach a $(( ), which is signed 64-bit
# and wraps rather than failing: TIMEOUT_SEC=18446744073709551633 reads as
# 17, so a 240s join budget becomes a deadline already in the past and the
# cycle gives up on the first poll. A digit regex cannot catch that, so the
# length is checked against the intmax ceiling before the text ever reaches
# arithmetic.
is_uint() {
	local LC_ALL=C
	[[ "$1" =~ ^[0-9]+$ ]] || return 1
	local d
	d="$(canon_uint "$1")"
	[[ -n "$d" ]] || return 0
	(( ${#d} < 19 )) && return 0
	(( ${#d} == 19 )) || return 1
	# 19 digits: the leading 18 fit on their own, so the last one decides
	# whether the whole value is over 9223372036854775807.
	local p=$((10#${d:0:18}))
	(( p < 922337203685477580 )) && return 0
	(( p > 922337203685477580 )) && return 1
	[[ "${d:18}" -le 7 ]]
}

# True for 1..65535, the same range the client enforces on 7DTD_CONNECT
# (ConnectTarget.TryParse). A value outside it is not a port the client can
# join: --port would be refused and the listener probe (":${PORT}\b") could
# never match, so the run would fail much later as a listen or join timeout
# instead of naming the bad value. Leading zeros are accepted as decimal.
is_tcp_port() {
	is_uint "$1" || return 1
	local d
	d="$(canon_uint "$1")"
	# 0 has no canonical digits. The port range is 5 digits, so a longer
	# value is out of range and never reaches the arithmetic below.
	[[ -n "$d" ]] || return 1
	(( ${#d} > 5 )) && return 1
	((10#$d >= 1 && 10#$d <= 65535))
}

# Upper bound on the client-mute poll window, in seconds. Both the helper's
# deadline (mono_sec + wait) and the value the launcher announces are signed
# 64-bit, so a wait near intmax wraps the deadline into the past and the poll
# gives up on its first check. An hour is far above any real window.
MAX_MUTE_WAIT_SECONDS=3600

# True for a mute poll window: 1..MAX_MUTE_WAIT_SECONDS. Shared so the
# launcher and the helper it calls never disagree about what a value means.
is_mute_wait() {
	is_uint "$1" || return 1
	local d
	d="$(canon_uint "$1")"
	[[ -n "$d" ]] || return 1
	((10#$d >= 1 && 10#$d <= MAX_MUTE_WAIT_SECONDS))
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
