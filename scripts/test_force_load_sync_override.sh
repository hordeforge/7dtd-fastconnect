#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="$ROOT/Source/ConnectMod/SkipIntroPatches.cs"
ENVFLAGS="$ROOT/Source/ConnectMod/EnvFlags.cs"
source "$ROOT/scripts/test_common.sh"

# Within ApplyForceLoadSync the ForceLoadSyncEnabled() gate must run before
# any reflection into LoadManager.forceLoadSync.
force_sync_order() {
    local source="$1" sig rest check_off field_off
    sig="$(grep -n 'internal static void ApplyForceLoadSync' "$source" | cut -d: -f1)"
    [[ -n "$sig" ]] || return 1
    rest="$(tail -n +"$sig" "$source")"
    check_off="$(printf '%s\n' "$rest" | grep -nm1 -F 'ForceLoadSyncEnabled()' | cut -d: -f1)"
    field_off="$(printf '%s\n' "$rest" | grep -nm1 -F 'GetField("forceLoadSync"' | cut -d: -f1)"
    [[ -n "$check_off" && -n "$field_off" ]] || return 1
    (( check_off < field_off ))
}

assert "names the force-load-sync override" \
    grep -q 'ForceLoadSyncEnv = "7DTD_CONNECT_FORCE_LOAD_SYNC"' "$SOURCE"
assert "keeps force-load-sync enabled by default" \
    grep -q 'string.IsNullOrWhiteSpace(value) || EnvFlags.IsSetOn(value)' "$SOURCE"
assert "delegates opt-outs to the shared env parser" \
    grep -q 'EnvFlags.IsSetOn(value)' "$SOURCE"
assert "accepts zero as an opt-out" \
    grep -q 'value == "0"' "$ENVFLAGS"
assert "accepts false-like opt-outs" \
    grep -q 'StringComparison.OrdinalIgnoreCase' "$ENVFLAGS"
assert "checks the override before changing LoadManager" \
    force_sync_order "$SOURCE"
assert "documents the Steam launch option" \
    grep -q 'env 7DTD_CONNECT_FORCE_LOAD_SYNC=0 mangohud %command%' "$ROOT/README.md"

finish
