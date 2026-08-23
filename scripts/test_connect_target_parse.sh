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
#     -connect=/-connect/+connect argv forms, +connect_lobby skip
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/Source/ConnectMod/ConnectTarget.cs"
STUBS="$ROOT/scripts/testdata/connect_target_stubs.cs"
HARNESS="$ROOT/scripts/testdata/connect_target_harness.cs"
source "$ROOT/scripts/test_common.sh"

if ! command -v mcs >/dev/null 2>&1; then
	echo "SKIP: mono mcs not found; cannot compile parse tests" >&2
	exit 0
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/7dtd-connect-parse.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

mcs -out:"$WORK/connect_target_tests.exe" -warn:1 "$SRC" "$STUBS" "$HARNESS" 1>&2

run_mode() {
	mono "$WORK/connect_target_tests.exe" "$@"
}

# expect_argv NAME EXPECTED_LINE -- ARGS...
expect_argv() {
	local name="$1" expected="$2"
	shift 3 # name, expected, "--"
	local got
	got="$(run_mode argv "$@" | head -n1)"
	if [[ "$got" == "$expected" ]]; then echo "PASS $name"
	else echo "FAIL $name (expected '$expected', got '$got')" >&2; FAILS=$((FAILS + 1)); fi
}

assert "TryParse / MergePortArg table" run_mode parse
assert "launch-context env resolution" run_mode launchctx
assert "log-safe flattening of launch targets" run_mode sanitize

TAB=$'\t'
expect_argv "argv -connect= picks target" \
	"OK${TAB}5.6.7.8${TAB}99${TAB}-connect=5.6.7.8:99" -- \
	-skipintro -connect=5.6.7.8:99
expect_argv "argv -connect <value> two-token form" \
	"OK${TAB}5.6.7.8${TAB}99${TAB}-connect 5.6.7.8:99" -- \
	-connect 5.6.7.8:99
expect_argv "argv +connect= form" \
	"OK${TAB}4.3.2.1${TAB}80${TAB}+connect=4.3.2.1:80" -- \
	+connect=4.3.2.1:80
expect_argv "argv flag match is case-insensitive" \
	"OK${TAB}::1${TAB}27030${TAB}-CONNECT=[::1]:27030" -- \
	-CONNECT=[::1]:27030
expect_argv "argv skips +connect_lobby token pair" \
	"OK${TAB}1.2.3.4${TAB}27025${TAB}-connect=1.2.3.4:27025" -- \
	+connect_lobby 10996666 -connect=1.2.3.4:27025
expect_argv "argv rejects unparseable -connect value" \
	"NO" -- \
	-connect="[not-a-target"

finish
