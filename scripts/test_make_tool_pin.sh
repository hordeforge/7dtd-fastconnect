#!/usr/bin/env bash
# Behavioral gate for scripts/assert_tool_pin.sh, which guards the Makefile's
# non-uv ruff/mypy/pytest paths: without uv those run a binary from PATH, so
# the gate accepts only the version pyproject.toml pins.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GUARD="$ROOT/scripts/assert_tool_pin.sh"
PYPROJECT="$ROOT/pyproject.toml"
source "$ROOT/scripts/test_common.sh"

WORK="$(scratch_mktemp "$ROOT" test_make_tool_pin)"
trap 'rm -rf "$WORK"' EXIT

# assert can only test success, so rejected versions are asserted through
# reject_rc: it succeeds only when the guard exited non-zero.
reject_rc() {
	set +e
	"$@" >/dev/null 2>&1
	local rc=$?
	set -e
	((rc != 0))
}

pin_of() {
    sed -n "s/^  \"$1==\([^\"]*\)\",$/\1/p" "$PYPROJECT" | head -1
}

RUFF_PIN="$(pin_of ruff)"
assert "pyproject pins ruff exactly" test -n "$RUFF_PIN"

# Stands in for the tool on PATH: given the version line to print, it echoes
# it when called with --version and exits with the code it is handed, so each
# case controls only what the guard judges.
cat >"$WORK/stub" <<'STUB'
#!/usr/bin/env bash
if [[ "${2:-}" == "--version" ]]; then echo "${1:-}"; fi
exit "${3:-0}"
STUB
chmod +x "$WORK/stub"

assert "accepts the pinned version" \
    "$GUARD" ruff "$WORK/stub" "ruff $RUFF_PIN"
assert "accepts a decorated version line" \
    "$GUARD" ruff "$WORK/stub" "mypy $RUFF_PIN (compiled: yes)"
assert "rejects a different version" \
    reject_rc "$GUARD" ruff "$WORK/stub" "ruff 0.0.1"
assert "rejects a version the pin is a prefix of" \
    reject_rc "$GUARD" ruff "$WORK/stub" "ruff ${RUFF_PIN}9"
assert "rejects a version command that fails" \
    reject_rc "$GUARD" ruff "$WORK/stub" "ruff $RUFF_PIN" 1
assert "rejects a version command that prints nothing" \
    reject_rc "$GUARD" ruff "$WORK/stub" ""
assert "rejects a call with no command" reject_rc "$GUARD" ruff
assert "--help exits 0 with a usage line" \
    grep -q '^Usage:' <("$GUARD" --help)

# The pytest call shape the Makefile uses: the command carries arguments of
# its own, so --version is appended to "python3 -m pytest", not to "python3".
cat >"$WORK/pytest_stub" <<'STUB'
#!/usr/bin/env bash
if [[ "${1:-}" == "-m" && "${2:-}" == "pytest" && "${3:-}" == "--version" ]]; then
    echo "pytest ${PYTEST_STUB_VERSION:-}"
    exit "${PYTEST_STUB_STATUS:-0}"
fi
exit 1
STUB
chmod +x "$WORK/pytest_stub"

assert "accepts the pinned version through python -m" \
    env PYTEST_STUB_VERSION="$(pin_of pytest)" \
    "$GUARD" pytest "$WORK/pytest_stub" -m pytest
assert "rejects a different version through python -m" \
    reject_rc env PYTEST_STUB_VERSION="0.0.1" \
    "$GUARD" pytest "$WORK/pytest_stub" -m pytest
assert "rejects a failing python -m version command" \
    reject_rc env PYTEST_STUB_VERSION="$(pin_of pytest)" PYTEST_STUB_STATUS=1 \
    "$GUARD" pytest "$WORK/pytest_stub" -m pytest

# A pin the guard cannot read exactly must fail, not match anything at all.
cat >"$WORK/pyproject.toml" <<'MANIFEST'
[dependency-groups]
dev = [
  "ruff>=0.16",
]
MANIFEST
assert "rejects a manifest with no exact pin" \
    reject_rc env PYPROJECT="$WORK/pyproject.toml" \
    "$GUARD" ruff "$WORK/stub" "ruff $RUFF_PIN"

finish
