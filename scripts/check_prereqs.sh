#!/usr/bin/env bash
# Preflight for `make test`: report every tool the offline suite needs that this
# machine lacks, name the command that installs it, and exit 1.
#
# Why this exists: the suite's own answer to a missing tool is a skip. A gate
# that cannot find zip, unzip, or jq exits 0, so `make test` prints a green on a
# machine where the reproducibility and audio-helper gates never ran. The
# contributor learns that from the diff three commits later, not from the run.
# The shell sources are linted by the same reasoning: that tool fails the run
# when it is missing, and this check names the rest.
#
# Builtins only, so `make doctor` can point it at a stub PATH and report on a
# toolchain that is not this shell's.
#
# Exit: 0 every required tool present, 1 otherwise, 2 on a usage error.

set -uo pipefail

usage() {
	printf '%s\n' \
		'Usage: check_prereqs.sh [--help]' \
		'' \
		'Report the system tools `make test` needs and name the ones this machine is' \
		'missing, with the package that installs each. Exits 1 when a required tool is' \
		'absent, so a missing tool fails the run instead of silently skipping its gate.' \
		'' \
		'  check_prereqs.sh          check this machine' \
		'  check_prereqs.sh --help   print this text and exit 0' \
		'' \
		'Environment:' \
		'  PATH   the toolchain to inspect (default: this shell'"'"'s PATH)'
}

case "${1-}" in
--help | -h)
	usage
	exit 0
	;;
"")
	;;
*)
	echo "check_prereqs.sh: unexpected argument: $1" >&2
	usage >&2
	exit 2
	;;
esac

MISSING=0

report() {
	local name="$1" hint="$2" path
	if path="$(command -v "$name" 2>/dev/null)"; then
		printf 'ok       %-10s %s\n' "$name" "$path"
	else
		printf 'missing  %-10s %s\n' "$name" "$hint" >&2
		MISSING=$((MISSING + 1))
	fi
}

# The C# harness has two lanes, and either one compiles and runs the real
# ConnectTarget: the dotnet SDK, or mcs with the mono runtime behind it. mcs
# alone is not a lane, so the pair is one requirement.
report_compiler() {
	local hint="$1"
	if command -v dotnet >/dev/null 2>&1; then
		printf 'ok       %-10s %s\n' "dotnet" "$(command -v dotnet)"
	elif command -v mcs >/dev/null 2>&1 && command -v mono >/dev/null 2>&1; then
		printf 'ok       %-10s %s\n' "mcs+mono" "$(command -v mcs)"
	else
		printf 'missing  %-10s %s\n' "compiler" "$hint" >&2
		MISSING=$((MISSING + 1))
	fi
}

# Advise only: without uv the suite falls back to ruff/mypy/yamllint/pytest on
# PATH, which assert_tool_pin.sh holds to the == pins in pyproject.toml.
advise() {
	local name="$1" hint="$2" path
	if path="$(command -v "$name" 2>/dev/null)"; then
		printf 'ok       %-10s %s\n' "$name" "$path"
	else
		printf 'advice   %-10s %s\n' "$name" "$hint" >&2
	fi
}

report shellcheck "the suite fails without it; apt-get install shellcheck, brew install shellcheck"
report zip "needed by the packaging gates; apt-get install zip, brew install zip"
report unzip "needed by the packaging gates; apt-get install unzip, brew install unzip"
report jq "needed by the audio-helper gates; apt-get install jq, brew install jq"
report_compiler "the ConnectTarget gate compiles the real C#; install the SDK band pinned in global.json (apt-get install dotnet-sdk-8.0), or apt-get install mono-devel for mcs+mono"

advise uv "no uv: the Python gates fall back to ruff/mypy/yamllint/pytest on PATH, each held to its == pin; install uv and run 'uv sync --frozen --group dev' for the same toolchain CI uses"

if ((MISSING > 0)); then
	printf 'doctor: %d required tool(s) missing; see above for the install command\n' "$MISSING" >&2
	exit 1
fi
echo "doctor: every required tool is present"
