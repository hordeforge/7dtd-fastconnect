#!/usr/bin/env bash
# Offline gate for the local .NET tool manifest, .config/dotnet-tools.json.
# dotnet-coverage is a build dependency of the coverage lane like the SDK is,
# so its version has to be stated in the tree the way the SDK band is (global
# .json) and the Python toolchain's pins are (pyproject.toml). Before the
# manifest existed the only copy of the number was a --version argument in a
# workflow file, and a local run was told to `dotnet tool install -g
# dotnet-coverage`, which resolves whatever the index serves that day.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MANIFEST="$ROOT/.config/dotnet-tools.json"
CI="$ROOT/.github/workflows/ci.yml"
COVERAGE="$ROOT/scripts/coverage-cs.sh"
source "$ROOT/scripts/test_common.sh"

assert "the local tool manifest exists" test -f "$MANIFEST"

# Read through jq rather than grepping: a malformed manifest has to fail here,
# not inside a dotnet command on a CI runner.
manifest_field() { jq -r "$1" "$MANIFEST" 2>/dev/null; }

assert "the manifest is JSON of schema version 1" \
	test "$(manifest_field '.version')" = 1
assert "the manifest names dotnet-coverage" \
	test "$(manifest_field '.tools["dotnet-coverage"] != null')" = true

# Counted, not read: a second tool is a second dependency nothing in this tree
# installs, and the assertion above still passes when one is added beside it.
tools="$(manifest_field '.tools | length')"
assert "dotnet-coverage is the only declared tool" test "$tools" -eq 1

VERSION="$(manifest_field '.tools["dotnet-coverage"].version')"
assert "the tool pins an exact version" \
	test -n "$VERSION" -a -z "${VERSION//[0-9.]/}"
assert "the tool pins three version components" \
	test "$(tr -cd '.' <<<"$VERSION" | wc -c)" -eq 2

# The pin has to be read from the manifest, not restated beside it: a second
# copy is the drift this manifest exists to remove.
assert "CI restores the tool from the manifest" \
	grep -q 'run: dotnet tool restore' "$CI"
assert "CI states no version of its own" \
	not_grep_re 'dotnet tool (install|update).*--version' "$CI"
assert "the coverage lane's skip hint names the manifest" \
	grep -q 'dotnet tool restore.*\.config/dotnet-tools\.json' "$COVERAGE"

finish
