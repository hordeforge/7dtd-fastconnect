#!/usr/bin/env bash
# Line coverage for the ConnectTarget offline gate, compiled with the dotnet
# SDK instead of mcs so coverlet/dotnet-coverage can instrument it. Mirrors
# scripts/test_connect_target_parse.sh: the same eight production sources plus
# the compiler-only stubs and the harness driver, run once per harness mode
# except the fuzz mode, which the behavioural gate owns and which contributes
# no coverage here. Output: coverage.cobertura.xml at the repo root (product sources are
# filtered to /Source/ when the badge renders).
set -euo pipefail

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
	cat <<'EOF'
Usage: coverage-cs.sh

Build the ConnectTarget offline gate with the dotnet SDK and collect a
Cobertura line-coverage report into coverage.cobertura.xml at the repo root.
Runs every harness mode except fuzz; the Makefile's coverage target then
renders the badge from it.

Exit status: 0 report written | 1 build or collect failure
             | 2 usage error.
             Skips with status 0 when dotnet or dotnet-coverage is absent.

Key env vars: none
EOF
	exit 0
fi

# Takes no arguments: an unknown word would otherwise fall through to a full
# SDK build and profiler run under a name the caller did not ask for.
if (( $# != 0 )); then
	echo "usage: ${0##*/} (takes no arguments; got $#)" >&2
	exit 2
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/scripts/test_common.sh"
work="$(scratch_mktemp "$root" 7dtd-connect-cov)"
trap 'rm -rf "$work"' EXIT

if ! command -v dotnet >/dev/null 2>&1; then
	echo "SKIP: dotnet SDK not found; cannot run the coverage lane" >&2
	exit 0
fi
if ! command -v dotnet-coverage >/dev/null 2>&1; then
	echo "SKIP: dotnet-coverage not found (dotnet tool restore --tool-path <dir>, version pinned in .config/dotnet-tools.json)" >&2
	exit 0
fi

source "$root/scripts/harness_csproj.sh"

emit_harness_csproj "$work/cov.csproj" "$root"

cd "$work"
# No output redirect: quiet verbosity is silent on success and must still show
# compile errors, otherwise a broken coverage build fails with no diagnosis.
dotnet build -c Release -v q --nologo
dll="$(find bin -name 'cov.dll' | head -1)"

modes=(argv argvenv automation connectready envflags forcesync launchctx parse playernames sanitize)
for m in "${modes[@]}"; do
	dotnet-coverage collect -f cobertura -o "cov-$m.xml" -- dotnet "$dll" "$m" > /dev/null 2>&1 || {
		echo "FAIL: harness mode $m under the coverage profiler" >&2
		exit 1
	}
done

# Remove the merged report first: a second run in the same tree must not
# depend on the merger overwriting a file it already wrote, and a failed
# merge must leave no previous report behind for the badge to render.
rm -f "$root/coverage.cobertura.xml"
dotnet-coverage merge -f cobertura -o "$root/coverage.cobertura.xml" cov-*.xml > /dev/null
echo "OK: $root/coverage.cobertura.xml"
