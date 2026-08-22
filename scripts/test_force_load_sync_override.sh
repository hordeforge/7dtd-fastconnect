#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="$ROOT/Source/ConnectMod/SkipIntroPatches.cs"
FAILS=0

assert() {
    local name="$1"
    shift
    if "$@"; then
        echo "PASS $name"
    else
        echo "FAIL $name" >&2
        FAILS=$((FAILS + 1))
    fi
}

assert "names the force-load-sync override" \
    grep -q 'ForceLoadSyncEnv = "7DTD_CONNECT_FORCE_LOAD_SYNC"' "$SOURCE"
assert "keeps force-load-sync enabled by default" \
    grep -q 'if (string.IsNullOrWhiteSpace(value)) return true;' "$SOURCE"
assert "accepts zero as an opt-out" \
    grep -q 'value != "0"' "$SOURCE"
assert "accepts false-like opt-outs" \
    grep -q 'StringComparison.OrdinalIgnoreCase' "$SOURCE"
assert "checks the override before changing LoadManager" \
    python3 - "$SOURCE" <<'PY'
import pathlib
import sys

source = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8")
method = source[source.index("internal static void ApplyForceLoadSync()") :]
raise SystemExit(0 if method.index("ForceLoadSyncEnabled()") < method.index("GetField(\"forceLoadSync\"") else 1)
PY
assert "documents the Steam launch option" \
    grep -q 'env 7DTD_CONNECT_FORCE_LOAD_SYNC=0 mangohud %command%' "$ROOT/README.md"

if ((FAILS > 0)); then
    echo "RESULT FAIL ($FAILS)" >&2
    exit 1
fi

echo "RESULT PASS"
