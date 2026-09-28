#!/usr/bin/env bash
# Copy the join-relevant lines of a client log into the harness control log.
#
# The client log is server-influenced text: a server name, a world string, or
# a chat line reaches it verbatim. The control log (client-lifecycle-*.txt) is
# the file join tooling greps for fixed markers ("result=", "==="), so a
# server-supplied line copied in raw could read as a harness marker. Two rules
# keep it from doing so, and both are needed:
#
#   * every copied line goes through sanitize_log_text (scripts/log_sanitize.sh,
#     which the caller sources), so a U+2028 or a C1 NEL inside a server line
#     cannot split it into two lines for a reader that breaks on those;
#   * every copied line is written behind a "  log: " prefix, so no copy can
#     start a line a marker grep takes, whatever the server put in it.
#
# A third rule is a privacy one, not a marker one. The copied lines carry the
# values the stock client and server log prints for a person: the display name
# on the login line, the peer IP address, the platform user id, and the player
# names inside game messages. The control log is the file a person attaches to
# a bug report, so redact_personal_log_text replaces those values before the
# copy is written. The unredacted log stays where the caller put it.
#
# Nothing is written to stdout: stdout is what zero_nre_join_loop.sh captures
# and greps for "^result=". The unsanitized copy of the log stays where the
# caller put it, so the artifact meant to be read verbatim is untouched.
#
# Usage: write_join_evidence <client_log_copy> <control_log>
#        copy_log_tail <log> <control_log> <lines> <label>
# Source this file; do not execute it.

# Lines the join harnesses care about, same set one_shot_join.sh listed inline.
readonly JOIN_EVIDENCE_RE='7dtd-fastconnect|LiteNetLib: Accepted|NCSimple|PlayerId|PlayerLogin|Spawned|Kicked|WorldInfo|PackageIds|[Ee]rror|ERR'

# How many evidence lines one cycle keeps; the client log reaches megabytes.
readonly JOIN_EVIDENCE_LINES=80

# What a redacted value is replaced with. A fixed token, so a reader can tell
# a redacted value from one the log never had.
readonly LOG_REDACTED='<redacted>'

# Replaces the value a stock log line holds for a person, keeping the line's
# label so it still reads as evidence.
#
# Each rule names the stock format string it comes from; they were read out of
# the shipped Assembly-CSharp.dll, not guessed:
#
#   "PlayerLogin: "   NetPackagePlayerLogin, the display name sent at login.
#                     This is the same value ModApi keeps out of the client log
#                     on purpose (docs/PRIVACY.md, "Never in the client log").
#   "Client IP: "     the peer address; an IP address identifies a person on a
#                     shared or NAT'd network.
#   "PlayerId({0}, {1})"  NetPackagePlayerLogin.CreateEntity, the entity id and
#                     the platform user id. The entity id is a per-session
#                     counter and stays; the platform id is a stable
#                     identifier (a real SteamID64, or the synthetic one
#                     AuthFallbackPatches derives), so it goes.
#   "Player '{0}'"    the game messages "Player '{0}' died" and
#                     "Player '{0}' killed by '{1}'", both player names.
#   "killed by '{1}'" the second operand of that kill message, a name too.
#   "Player {0} disconnected after {1} minutes"  the leave message, an
#                     unquoted name.
#
# Applied after sanitize_log_text, so a value cannot hide a name from one rule
# behind a character the other already flattened.
redact_personal_log_text() {
	local text="$1" head matched entity quoted_name
	# Each pattern is held in a variable rather than written into [[ =~ ]]
	# directly: bash treats quoting inside a [[ ]] regex as literal text, which
	# mangles the character classes.
	local re_player_id='PlayerId\(([0-9]+),[[:space:]]*[0-9]+\)'
	local re_player_quoted="Player[[:space:]]+'[^']*'"
	local re_killer_quoted="killed by '[^']*'"
	local re_player_left='Player[[:space:]]+[^[:space:]]+[[:space:]]+disconnected'
	if [[ "$text" == *"PlayerLogin: "* ]]; then
		head="${text%%PlayerLogin: *}"
		text="$head"'PlayerLogin: '"$LOG_REDACTED"
	fi
	if [[ "$text" == *"Client IP: "* ]]; then
		head="${text%%Client IP: *}"
		text="$head"'Client IP: '"$LOG_REDACTED"
	fi
	if [[ "$text" =~ $re_player_id ]]; then
		matched="${BASH_REMATCH[0]}"
		entity="${BASH_REMATCH[1]}"
		text="${text/"$matched"/"PlayerId($entity, $LOG_REDACTED)"}"
	fi
	# Both operands of a game message are player names, so each quoted span
	# after its label goes, not only the one the message opens with.
	if [[ "$text" =~ $re_player_quoted ]]; then
		quoted_name="${BASH_REMATCH[0]}"
		text="${text/"$quoted_name"/"Player '$LOG_REDACTED'"}"
	fi
	if [[ "$text" =~ $re_killer_quoted ]]; then
		quoted_name="${BASH_REMATCH[0]}"
		text="${text/"$quoted_name"/"killed by '$LOG_REDACTED'"}"
	fi
	if [[ "$text" =~ $re_player_left ]]; then
		matched="${BASH_REMATCH[0]}"
		# Keep the verb: only the name between "Player " and " disconnected"
		# goes, so the line still reads as a leave event.
		text="${text/"$matched"/"Player $LOG_REDACTED disconnected"}"
	fi
	printf '%s' "$text"
}

# One copied line into the control log: flattened, redacted, and behind the
# "  log: " prefix, so the three rules the header describes cannot be applied
# by a caller that forgets one of them.
copy_log_line() {
	local text
	text="$(sanitize_log_text "$1")"
	printf '  log: %s\n' "$(redact_personal_log_text "$text")"
}

# Copies the last N lines of any log under the same three rules. The server log
# goes through here too: it carries the same display names and peer addresses
# the client log does, and both harnesses print or keep its tail when the
# server never listened.
#
# A control_log of "-" writes to stdout, for the caller whose only output
# channel is stdout. Nothing here writes to stdout otherwise, because stdout is
# what zero_nre_join_loop.sh captures and greps for "^result=".
#
# Usage: copy_log_tail <log> <control_log|-> <lines> <label>
copy_log_tail() {
	local log="$1" control_log="$2" lines="$3" label="$4" line
	[[ "$control_log" == "-" ]] && control_log=/dev/stdout
	{
		printf 'last %s %s lines:\n' "$lines" "$label"
		if [[ ! -r "$log" ]]; then
			printf '  log: (%s log unavailable: %s)\n' "$label" "$log"
			return 0
		fi
		while IFS= read -r line; do
			copy_log_line "$line"
		done < <(tail -n "$lines" "$log" 2>/dev/null)
	} >>"$control_log"
}

write_join_evidence() {
	local client_log="$1" control_log="$2" line
	{
		printf 'key client lines:\n'
		printf '  log: (raw copy: %s)\n' "$client_log"
		# The caller's copy of the client log is allowed to fail (see
		# one_shot_join.sh), and grep's "No such file" is discarded below. An
		# empty section would be indistinguishable from a log with no matching
		# lines, so name the missing artifact instead.
		if [[ ! -r "$client_log" ]]; then
			printf '  log: (client log unavailable: %s)\n' "$client_log"
			return 0
		fi
		while IFS= read -r line; do
			copy_log_line "$line"
		done < <(grep -En "$JOIN_EVIDENCE_RE" "$client_log" 2>/dev/null | head -"$JOIN_EVIDENCE_LINES")
	} >>"$control_log"
}
