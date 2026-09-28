#!/usr/bin/env bash
# Structural tests for the 7DTD_PLAYER_NAME override (no live game required).
# Greps that could match elsewhere in ModApi.cs (GamePrefs.Instance?.Save() is
# not unique to this method) are scoped to the ApplyPlayerNameOverride body, so
# an unrelated occurrence cannot satisfy them.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="$ROOT/Source/ConnectMod/ModApi.cs"
source "$ROOT/scripts/test_common.sh"

METHOD="$(sed -n '/static void ApplyPlayerNameOverride()/,/^        }$/p' "$SOURCE")"
assert "ApplyPlayerNameOverride method exists" test -n "$METHOD"

# Scoped to InitMod, not the file: a whole-file grep for the name is satisfied
# by a call from any other method, so it would pass with the override moved
# out of the init path, which is the one thing this gate has to pin.
INITMOD="$(sed -n '/public void InitMod(Mod _modInstance)/,/^        }$/p' "$SOURCE")"
assert "InitMod method exists" test -n "$INITMOD"

body_contains() { [[ "$METHOD" == *"$1"* ]]; }
body_omits() { [[ "$METHOD" != *"$1"* ]]; }
initmod_contains() { [[ "$INITMOD" == *"$1"* ]]; }

assert "names the opt-in environment variable" grep -q 'PlayerNameEnv = "7DTD_PLAYER_NAME"' "$SOURCE"
# The call is a method group passed to the guarded-step helper, so accept both
# that form and a plain call; what is asserted is that InitMod runs the
# override, not how the call is spelled.
assert "InitMod runs the player-name override" \
	initmod_contains 'ApplyPlayerNameOverride'
assert "falls back to a generated name when the variable is unset" \
	body_contains 'PlayerNames.Resolve()'
assert "normalizes an env-supplied name through the shared helper" \
	body_contains 'PlayerNames.Normalize(requested)'
assert "normalization clamps the length and drops invisible characters" \
	grep -q 'internal static string Normalize' "$ROOT/Source/ConnectMod/PlayerNames.cs"
# The cap is counted in code points, so a UTF-16-unit cap (a second
# truncation rule beside the shared one) must not reappear here; it would cut
# an astral name that the cap is documented to allow.
assert "no UTF-16-unit length cap is reintroduced" \
	not_grep 'name.Length <= MaxLength' "$ROOT/Source/ConnectMod/PlayerNames.cs"
assert "the cap and its code-point unit live in PlayerNames" \
	grep -q 'MaxLength = 24' "$ROOT/Source/ConnectMod/PlayerNames.cs"
assert "the cap normalizes and cuts without splitting a surrogate pair" \
	grep -q 'TextUtil.TruncateToCodePoints(TextUtil.NormalizeFormC(name), MaxLength)' \
		"$ROOT/Source/ConnectMod/PlayerNames.cs"
assert "uses the stock player-name preference inside the override" \
	body_contains 'GamePrefs.Set(EnumGamePrefs.PlayerName, requested)'
assert "persists the preference inside the override" \
	body_contains 'GamePrefs.Instance?.Save();'
assert "keeps the applied name out of the log (the log ships in bug reports)" \
	body_omits 'player name from " + PlayerNameEnv + "='
assert "logs which source supplied the name instead" \
	body_contains 'player name applied from'
assert "documents the separate-client mechanism" grep -q 'Local player identity for an isolated test client' "$ROOT/README.md"
# docs/PRIVACY.md states where the name goes (the stored pref, the server, its
# logs) and that it never reaches the client log. Both halves are claims about
# this code, so a change to the name path that leaves the page describing the
# old flow fails here rather than shipping a doc that misstates it.
PRIVACY="$ROOT/docs/PRIVACY.md"
assert "the data-handling page exists" test -f "$PRIVACY"
assert "the page names the pref the name is stored in" \
	grep -q 'EnumGamePrefs.PlayerName' "$PRIVACY"
assert "the page says the name never reaches the client log" \
	grep -q 'Never in the client log' "$PRIVACY"
doc_link_lands() {
	local file="$1" ref="$2" needle="$3"
	grep -q "$file:$ref" "$PRIVACY" || return 1
	[[ "$(sed -n "${ref}p" "$ROOT/Source/ConnectMod/$file")" == *"$needle"* ]]
}

# A line reference that no longer points at what the page claims is worse than
# a missing one, so the reference is resolved and the named line is read: the
# page cannot drift onto an unrelated statement unnoticed.
assert "the page links the name path back to the code" \
	doc_link_lands ModApi.cs 196 'player name applied from'
assert "the page covers the synthetic id the Steam-less path sends" \
	doc_link_lands AuthFallbackPatches.cs 73 'static PlatformUserIdentifierAbs SyntheticId()'

finish
