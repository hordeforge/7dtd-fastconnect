#!/usr/bin/env bash
# Behavioral tests for ConnectTarget parsing (headless; no game install needed).
#
# Compiles the REAL production Source/ConnectMod/ConnectTarget.cs together with
# compiler-only game-API stubs (testdata/connect_target_stubs.cs) and a small
# C# driver (testdata/connect_target_harness.cs), then asserts:
#   - TryParse: valid targets, rejections, default port, steam:// stripping,
#     bracketed/bare IPv6, port bounds
#   - MergePortArg: optional second console token merged only into a portless
#     host, steam:// scheme stripped first
#   - SanitizeForLog: control characters flattened so a crafted target value
#     cannot forge extra client-log lines (join harnesses grep those markers)
#   - TryFromLaunchContext: 7DTD_CONNECT resolution, invalid-env rejection,
#     -connect=/-connect/+connect argv forms, +connect_lobby skip, and the
#     documented precedence (7DTD_CONNECT beats -connect= argv)
#   - EnvFlags: behavioral opt-out/opt-in truthiness table (gates
#     AutomationMode and force-load-sync), not just source-text greps
#   - ConnectReady: gate state machine incl. already-connected short-circuit
#     and warn-once expiry notes
#   - PlayerNames: fallback identity invariants (never empty, trimmed, capped)
#   - AutomationMode: decision table (launch-context detection vs explicit
#     opt-in/opt-out), one process per case
#   - Fuzz: seeded grammar-biased generator asserting invariants over
#     TryParse/MergePortArg/SanitizeForLog/TryFromLaunchContext (totality,
#     bounded ports, single-line log/source, cross-port merge consistency)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/7dtd-connect-parse.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

# Two compile lanes for the same sources (scripts/harness_csproj.sh is the
# single list): mono mcs + mono runtime where present, else the dotnet SDK
# (same net8.0 project the coverage lane uses). Skipping requires BOTH to be
# missing, so CI runners with only a dotnet SDK still run these tests.
EXE=""
RUN=()
if command -v mcs >/dev/null 2>&1 && command -v mono >/dev/null 2>&1; then
	source "$ROOT/scripts/harness_csproj.sh"
	EXE="$WORK/connect_target_tests.exe"
	sources=()
	while IFS= read -r f; do sources+=("$f"); done < <(harness_sources "$ROOT")
	mcs -out:"$EXE" -warn:1 "${sources[@]}" 1>&2
	RUN=(mono "$EXE")
elif command -v dotnet >/dev/null 2>&1; then
	source "$ROOT/scripts/harness_csproj.sh"
	emit_harness_csproj "$WORK/connect_target_tests.csproj" "$ROOT"
	# No output redirect: quiet verbosity is silent on success and must still
	# show compile errors, otherwise a broken harness build fails undiagnosed.
	(cd "$WORK" && dotnet build -c Release -v q --nologo)
	EXE="$(cd "$WORK" && find bin -name 'connect_target_tests.dll' | head -1)"
	if [[ -z "$EXE" ]]; then
		echo "FAIL: dotnet lane produced no connect_target_tests.dll" >&2
		exit 1
	fi
	RUN=(dotnet "$WORK/$EXE")
else
	echo "SKIP: neither mono (mcs+mono) nor a dotnet SDK found; cannot compile parse tests" >&2
	exit 0
fi

run_mode() {
	# Bash cannot assign names starting with a digit, so the precedence cases
	# pin 7DTD_CONNECT through env(1) via this ordinary-name variable.
	if [[ -n "${ENV_TARGET:-}" ]]; then
		env 7DTD_CONNECT="$ENV_TARGET" "${RUN[@]}" "$@"
	else
		"${RUN[@]}" "$@"
	fi
}

# expect_argv MODE NAME EXPECTED_LINE -- ARGS...
expect_argv() {
	local mode="$1" name="$2" expected="$3"
	shift 4 # mode, name, expected, "--"
	local got
	got="$(run_mode "$mode" "$@" | head -n1)"
	if [[ "$got" == "$expected" ]]; then echo "PASS $name"
	else echo "FAIL $name (expected '$expected', got '$got')" >&2; FAILS=$((FAILS + 1)); fi
}

assert "TryParse / MergePortArg table" run_mode parse
assert "fuzz invariants over the launch-target grammar" run_mode fuzz
assert "launch-context env resolution" run_mode launchctx
assert "log-safe flattening of launch targets" run_mode sanitize
assert "EnvFlags opt-out/opt-in truthiness table" run_mode envflags
assert "ConnectReady gate state machine" run_mode connectready
assert "PlayerNames fallback invariants (never empty, capped, trimmed)" run_mode playernames
assert "force-load-sync default-on / opt-out / snapshot contract" run_mode forcesync

# AutomationMode gates every automation patch; its decision table is
# documented on AutomationMode.Detect: unset resolves from the launch
# context (7DTD_CONNECT/-connect present means on), explicit values ride the
# EnvFlags truthiness contract, and an explicit opt-out beats a detected
# target. One process per case because detection is static-readonly.
expect_argv automation "automation off with nothing configured" "OFF" --
expect_argv automation "automation auto-on from detected launch target" "ON" -- conn
expect_argv automation "automation explicit opt-in value" "ON" -- auto=1
expect_argv automation "automation unknown value opts in" "ON" -- auto=sure
expect_argv automation "automation explicit opt-out value" "OFF" -- auto=0
expect_argv automation "automation opt-out beats detected target" "OFF" -- conn auto=0
expect_argv automation "automation false-like value beats detected target" "OFF" -- conn auto=off

TAB=$'\t'
expect_argv argv "argv -connect= picks target" \
	"OK${TAB}5.6.7.8${TAB}99${TAB}-connect=5.6.7.8:99" -- \
	-skipintro -connect=5.6.7.8:99
expect_argv argv "argv -connect <value> two-token form" \
	"OK${TAB}5.6.7.8${TAB}99${TAB}-connect 5.6.7.8:99" -- \
	-connect 5.6.7.8:99
expect_argv argv "argv +connect= form" \
	"OK${TAB}4.3.2.1${TAB}80${TAB}+connect=4.3.2.1:80" -- \
	+connect=4.3.2.1:80
expect_argv argv "argv flag match is case-insensitive" \
	"OK${TAB}::1${TAB}27030${TAB}-CONNECT=[::1]:27030" -- \
	-CONNECT=[::1]:27030
expect_argv argv "argv skips +connect_lobby token pair" \
	"OK${TAB}1.2.3.4${TAB}27025${TAB}-connect=1.2.3.4:27025" -- \
	+connect_lobby 10996666 -connect=1.2.3.4:27025
expect_argv argv "argv rejects unparseable -connect value" \
	"NO" -- \
	-connect="[not-a-target"

# Precedence: the documented resolution order is env first, then argv. The
# argvenv mode keeps 7DTD_CONNECT (pinned here via ENV_TARGET) active, so an
# implementation that starts preferring -connect= flips the join target and
# fails these.
ENV_TARGET=9.9.9.9:1234 expect_argv argvenv "env target beats -connect= flag" \
	"OK${TAB}9.9.9.9${TAB}1234${TAB}7DTD_CONNECT=9.9.9.9:1234" -- \
	-connect=1.2.3.4:99
ENV_TARGET=9.9.9.9:1234 expect_argv argvenv "env target beats two-token -connect" \
	"OK${TAB}9.9.9.9${TAB}1234${TAB}7DTD_CONNECT=9.9.9.9:1234" -- \
	-connect 1.2.3.4:99

finish
