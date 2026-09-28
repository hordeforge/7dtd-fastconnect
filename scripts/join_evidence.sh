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
# Nothing is written to stdout: stdout is what zero_nre_join_loop.sh captures
# and greps for "^result=". The unsanitized copy of the log stays where the
# caller put it, so the artifact meant to be read verbatim is untouched.
#
# Usage: write_join_evidence <client_log_copy> <control_log>
# Source this file; do not execute it.

# Lines the join harnesses care about, same set one_shot_join.sh listed inline.
readonly JOIN_EVIDENCE_RE='7dtd-fastconnect|LiteNetLib: Accepted|NCSimple|PlayerId|PlayerLogin|Spawned|Kicked|WorldInfo|PackageIds|[Ee]rror|ERR'

# How many evidence lines one cycle keeps; the client log reaches megabytes.
readonly JOIN_EVIDENCE_LINES=80

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
			printf '  log: %s\n' "$(sanitize_log_text "$line")"
		done < <(grep -En "$JOIN_EVIDENCE_RE" "$client_log" 2>/dev/null | head -"$JOIN_EVIDENCE_LINES")
	} >>"$control_log"
}
