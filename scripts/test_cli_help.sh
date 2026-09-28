#!/usr/bin/env bash
# CLI contract of the lifecycle scripts: every user-facing entry point answers
# -h/--help on stdout with status 0 before touching disk or processes, and
# usage errors exit 2 (same convention as launch_client.sh's GFX_API gate).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

# Help must print a usage line to stdout and exit 0. Run through a function so
# set -e does not abort on the probe itself.
run_help() {
	set +e
	"$@" >/dev/null 2>&1
	local rc=$?
	set -e
	return "$rc"
}

for script in launch_client.sh one_shot_join.sh zero_nre_join_loop.sh \
	restart_pair.sh mute_client_audio.sh unmute_client_audio.sh \
	repro_zip.sh package.sh; do
	assert "$script --help exits 0" run_help "$ROOT/scripts/$script" --help
	assert "$script -h exits 0" run_help "$ROOT/scripts/$script" -h
	help_out="$("$ROOT/scripts/$script" --help 2>/dev/null)"
	assert "$script --help prints a usage line to stdout" \
		grep -q '^Usage:' <<<"$help_out"
done

# Help must be side-effect free: no scratch dir, no world dir creation, and
# nothing on stdout for the helpers' normal chatter channels to clobber.
SCRATCH_PROBE="$(scratch_mktemp "$ROOT" cli-help)"
trap 'rm -rf "$SCRATCH_PROBE"' EXIT
SCRATCH="$SCRATCH_PROBE/scratch" "$ROOT/scripts/one_shot_join.sh" --help >/dev/null
assert "one_shot_join.sh --help creates no scratch dir" \
	test ! -e "$SCRATCH_PROBE/scratch"
"$ROOT/scripts/zero_nre_join_loop.sh" --help >/dev/null
"$ROOT/scripts/restart_pair.sh" --help "$SCRATCH_PROBE/world" >/dev/null
assert "restart_pair.sh --help creates no world dir" \
	test ! -e "$SCRATCH_PROBE/world"
# package.sh --help must answer before feature-testing zip or invoking make.
"$ROOT/scripts/package.sh" --help >/dev/null
"$ROOT/scripts/repro_zip.sh" --help >/dev/null

# Usage errors exit 2, not 1: distinguishable from general failures by scripts
# consuming this repo's harnesses. All three probes exit before any teardown
# (pkill/mkdir) runs. usage_rc is an assert-style predicate: it succeeds only
# when the wrapped script exited 2, since assert can only test success.
usage_rc() {
	set +e
	"$@" >/dev/null 2>&1
	local rc=$?
	set -e
	((rc == 2))
}
usage_err() {
	{ "$@" >/dev/null; } 2>&1
}
not_usage_rc() {
	set +e
	"$@" >/dev/null 2>&1
	local rc=$?
	set -e
	((rc != 2))
}
assert "repro_zip.sh wrong argc exits 2" \
	usage_rc "$ROOT/scripts/repro_zip.sh" only-one-arg
assert "repro_zip.sh names both arguments" \
	grep -q '<stage_dir> <out.zip>' <(usage_err "$ROOT/scripts/repro_zip.sh")
assert "restart_pair.sh missing world exits 2" \
	usage_rc "$ROOT/scripts/restart_pair.sh"
assert "restart_pair.sh prints usage on missing arg" \
	grep -q 'usage:' <(usage_err "$ROOT/scripts/restart_pair.sh")
assert "restart_pair.sh non-numeric port exits 2" \
	usage_rc "$ROOT/scripts/restart_pair.sh" "$SCRATCH_PROBE/not-created" not-a-port
# HOST is validated next to PORT, before the pkill sweep, so a bad one cannot
# leave the previous pair dead with nothing relaunched.
assert "restart_pair.sh whitespace host exits 2" \
	usage_rc env HOST='a b' "$ROOT/scripts/restart_pair.sh" "$SCRATCH_PROBE/not-created" 27025
assert "restart_pair.sh names the host rule" \
	grep -q 'host must be a single word' \
	<(usage_err env HOST='a b' "$ROOT/scripts/restart_pair.sh" "$SCRATCH_PROBE/not-created" 27025)
# Unset and empty mean the documented loopback default, so neither is a usage
# error: the run stops later, on the missing zdtd binary, with status 1.
assert "restart_pair.sh empty host falls back to the default" \
	not_usage_rc env HOST='' "$ROOT/scripts/restart_pair.sh" "$SCRATCH_PROBE/not-created" 27025
assert "restart_pair.sh points the client at HOST, not a hardcoded loopback" \
	grep -qF '7DTD_CONNECT="$HOST:$PORT"' "$ROOT/scripts/restart_pair.sh"
assert "restart_pair.sh documents HOST in its help" \
	grep -q '^  HOST' <("$ROOT/scripts/restart_pair.sh" --help)

finish
