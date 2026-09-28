#!/usr/bin/env bash
# Behavioral gate for scripts/assert_tool_pin.sh, which guards the Makefile's
# non-uv ruff/mypy/pytest paths: without uv those run a binary from PATH, so
# the gate accepts only the version pyproject.toml pins. The uv lane is
# checked here too, for the same pin from the other side: it must install the
# locked versions from the named index rather than whatever pyproject alone
# says, and it must fail rather than run when the two files disagree.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GUARD="$ROOT/scripts/assert_tool_pin.sh"
PYPROJECT="$ROOT/pyproject.toml"
MAKEFILE="$ROOT/Makefile"
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

# The uv lane carries the same pin from the other side: it installs what
# uv.lock resolved, so the lock has to be what gets installed, checked against
# the manifest, and fetched from a named source.
LOCK="$ROOT/uv.lock"

assert "no uv invocation installs the gates with --frozen" \
	reject_rc grep -Eq 'uv (run|sync) .*--frozen' "$MAKEFILE"
assert "the Makefile runs pytest with --locked" \
	grep -q 'PYTEST := uv run --locked --group dev' "$MAKEFILE"
assert "the Makefile runs the lint and type lane with --locked" \
	grep -q 'uv run --locked --group dev mypy --strict' "$MAKEFILE"
assert "the setup instructions install with --locked" \
	grep -q 'uv sync --locked --group dev' "$ROOT/README.md"

assert "the Python toolchain resolves from pypi.org" \
	grep -q '^url = "https://pypi.org/simple"$' "$PYPROJECT"
# Counted, not read: a second index is a second place a mirror can be named,
# and the assertion above still passes when one is added beside it.
indexes="$(awk '/^\[\[tool\.uv\.index\]\]/ { n++ } END { print n + 0 }' "$PYPROJECT")"
assert "pypi.org is the only index uv resolves from" test "$indexes" -eq 1
assert "that index is the default" \
	grep -qx 'default = true' "$PYPROJECT"
assert "a uv too old for the lock is refused" \
	grep -q '^required-version = ">=' "$PYPROJECT"

# uv.lock is what makes that install verifiable, so an artifact entry without
# a digest names bytes no file in the tree vouches for. Counted, not sampled:
# one unhashed wheel among hundreds is the case worth failing on.
unhashed="$(awk '
    /url = "https:\/\/files\.pythonhosted\.org/ && !/hash = "sha256:/ { n++ }
    END { print n + 0 }' "$LOCK")"
assert "every artifact in uv.lock carries a sha256" test "$unhashed" -eq 0

finish
