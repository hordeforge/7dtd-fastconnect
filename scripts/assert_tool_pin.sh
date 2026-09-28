#!/usr/bin/env bash
# Refuse a fallback gate run whose tool is not the one the project pins.
#
# The Makefile runs ruff/mypy/pytest through `uv run --frozen`, which installs
# the exact versions from pyproject.toml's [dependency-groups] dev table and
# verifies them against the sha256 hashes in uv.lock. When uv is missing the
# Makefile falls back to whatever is on PATH; that binary is unpinned and its
# provenance unknown, so a fallback run must at least be the pinned version or
# it gates the project on a toolchain CI never runs.
#
# Usage: assert_tool_pin.sh <package> <command> [args...]
#   <package> key in pyproject.toml's [dependency-groups] dev table
#   <command> command whose first line reports the version under --version,
#            e.g. "ruff" or "python3 -m pytest"
#
# Env: PYPROJECT  manifest to read the pin from (default: the repo's)
#
# Exit status: 0 the tool reports the pinned version | 1 pin unreadable, the
# version command failed, or the versions differ | 2 wrong argument count.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MANIFEST="${PYPROJECT:-$ROOT/pyproject.toml}"

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
	cat <<'EOF'
Usage: assert_tool_pin.sh <package> <command> [args...]

Check that <command> reports the exact version pyproject.toml pins for
<package> in [dependency-groups] dev, so the Makefile's non-uv gate paths
cannot run an unpinned tool from PATH.

Exit status: 0 version matches the pin | 1 pin unreadable, the version
command failed, or the versions differ | 2 usage error.
EOF
	exit 0
fi

if [[ $# -lt 2 ]]; then
	echo "usage: ${0##*/} <package> <command> [args...] (got $# argument(s); needs at least 2)" >&2
	exit 2
fi

package="$1"
shift

# The dev table pins exactly ("ruff==0.16.6"), so the line is the only place a
# version can come from; an unpinned or ranged entry yields nothing and fails
# below rather than passing on an empty match.
pin="$(sed -n "s/^  \"$package==\([^\"]*\)\",$/\1/p" "$MANIFEST" | head -1)"
if [[ -z "$pin" ]]; then
	echo "ERROR: no exact '$package==' pin in $MANIFEST; cannot verify the tool" >&2
	exit 1
fi

if ! first_line="$("$@" --version 2>/dev/null | head -1)"; then
	echo "ERROR: '$* --version' failed; cannot verify it against $package==$pin" >&2
	exit 1
fi

# "mypy 2.3.1 (compiled: yes)" -> 2.3.1; "ruff 0.16.6" -> 0.16.6.
have="$(printf '%s\n' "$first_line" | grep -oE '[0-9]+(\.[0-9]+)*' | head -1)"
if [[ "$have" != "$pin" ]]; then
	echo "ERROR: '$*' reports $have, $package is pinned to $pin in $MANIFEST;" \
		"run this gate with uv on PATH (make test) so the pinned tool is used" >&2
	exit 1
fi
