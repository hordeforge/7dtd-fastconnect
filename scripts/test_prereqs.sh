#!/usr/bin/env bash
# Behavioral gate for scripts/check_prereqs.sh, the preflight `make test` runs
# so a missing system tool is named instead of turning its gate into a silent
# skip. The suite is inspected through a stub PATH holding only the tools a
# case should find, which is the only way to exercise the missing-tool branch
# on a machine that has everything installed.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"

WORK="$(scratch_mktemp "$ROOT" prereqs)"
trap 'rm -rf "$WORK"' EXIT
STUB="$WORK/stub"
mkdir -p "$STUB"

# Every tool the preflight looks for, so a case can withhold exactly the ones
# it is about and still let the others pass.
TOOLS=(bash shellcheck zip unzip jq dotnet mcs mono uv)

# Symlink the found tools into the stub PATH, skipping every name in "$@".
link_all_but() {
	local tool target withheld
	rm -f "$STUB"/* 2>/dev/null || true
	for tool in "${TOOLS[@]}"; do
		for withheld in "$@"; do
			[ "$tool" = "$withheld" ] && continue 2
		done
		target="$(command -v "$tool" 2>/dev/null || true)"
		[ -n "$target" ] || continue
		ln -sf "$target" "$STUB/$tool"
	done
}

# Run the preflight with PATH reduced to the stub, so it sees only the tools the
# case planted. Its own command set is bash builtins, so a stub PATH carrying
# no coreutils is enough.
run_check() {
	set +e
	env -i PATH="$STUB" "$ROOT/scripts/check_prereqs.sh" >"$WORK/out" 2>"$WORK/err"
	local rc=$?
	set -e
	cat "$WORK/out" "$WORK/err" >"$WORK/all"
	return "$rc"
}

# A machine with the full toolchain: every line says ok, and the run passes.
link_all_but
assert "every tool present exits 0" run_check
assert "a complete toolchain reports the tools it found" \
	grep -q '^ok .*shellcheck' "$WORK/all"
assert "a complete toolchain names no missing tool" not_grep '^missing' "$WORK/all"

# One tool withheld at a time: the run fails, and the report names the tool and
# the install command rather than leaving a bare nonzero status.
for tool in shellcheck zip unzip jq; do
	link_all_but "$tool"
	assert_fails "$tool missing exits 1" run_check
	assert "$tool missing is named in the report" \
		grep -q "^missing  $tool" "$WORK/all"
	assert "$tool missing names an install command" \
		grep -qE "^missing  $tool.*(apt-get|brew)" "$WORK/all"
	assert "$tool missing still reports the tools that are present" \
		grep -q '^ok ' "$WORK/all"
done

# The C# harness has two lanes and either one compiles and runs the real
# ConnectTarget: the dotnet SDK, or mcs with the mono runtime behind it. Half a
# pair is not a lane.
link_all_but mono dotnet mcs
assert_fails "no C# compiler exits 1" run_check
assert "no C# compiler is reported as a missing compiler" \
	grep -q '^missing  compiler' "$WORK/all"

link_all_but mono dotnet
assert_fails "mcs without mono exits 1" run_check

link_all_but mcs dotnet
assert_fails "mono without mcs exits 1" run_check

link_all_but mono
assert "dotnet alone satisfies the compiler requirement" run_check

link_all_but mcs
assert "mcs+mono satisfies the compiler requirement" run_check

# uv is advice, not a requirement: the suite falls back to the pinned tools on
# PATH, so a machine without uv must still pass.
link_all_but uv
assert "uv missing still exits 0" run_check
assert "uv missing is advice, not a missing tool" not_grep '^missing  uv' "$WORK/all"
assert "uv missing names the fallback" grep -q '^advice   uv' "$WORK/all"

finish
