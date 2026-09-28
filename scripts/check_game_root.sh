#!/usr/bin/env bash
# Preflight for the targets that need a game install: `make build`, and
# through it `make install` and `make package`.
#
# Why this exists: the build references the game's Assembly-CSharp.dll and
# 0Harmony.dll by HintPath, and a HintPath that resolves to nothing is not an
# error MSBuild reports. The compile then fails with one CS0246 per game type
# the mod touches (155 of them on the current tree), which reads as broken
# source rather than as a missing install, and the first-run contributor has
# no way to tell the two apart. This names the file that is not there and the
# GAME= override that points at the right one.
#
# Not the same check as check_prereqs.sh: that one covers the offline suite,
# which needs no game install, and `make doctor` stays runnable without one.
#
# Usage: check_game_root.sh [game_root]   (default: $GAME, else the stock path)
#
# Exit: 0 the install has every referenced assembly | 1 an install is missing
# or incomplete | 2 on a usage error.

set -uo pipefail

STOCK_GAME="$HOME/.local/share/Steam/steamapps/common/7 Days To Die"

usage() {
	printf '%s\n' \
		'Usage: check_game_root.sh [--help] [game_root]' \
		'' \
		'Report whether the game install the mod compiles against is present and' \
		'complete. Names the missing directory or assembly, so `make build` fails' \
		'with the reason instead of a wall of CS0246 errors. Exits 1 when anything' \
		'is missing.' \
		'' \
		'  check_game_root.sh [game_root]  check that install (default: $GAME,' \
		'                                else the stock Steam client path)' \
		'  check_game_root.sh --help       print this text and exit 0' \
		'' \
		'Environment:' \
		'  GAME  the game install, the same override `make build GAME=` takes'
}

# Exact argc, like every other entry point here: a third word (a mistyped flag,
# a pasted command line) is a usage error rather than a silently dropped path
# that would leave the check reporting on an install nobody named.
if (( $# > 2 )); then
	echo "check_game_root.sh: takes at most 2 arguments; got $#" >&2
	usage >&2
	exit 2
fi

case "${1-}" in
-h | --help)
	usage
	exit 0
	;;
"")
	;;
-*)
	echo "check_game_root.sh: not a game install path: $1" >&2
	usage >&2
	exit 2
	;;
esac

GAME_ROOT="${1:-${GAME:-$STOCK_GAME}}"
MANAGED="$GAME_ROOT/7DaysToDie_Data/Managed"
HARMONY="$GAME_ROOT/Mods/0_TFP_Harmony/0Harmony.dll"

MISSING=0

# Every assembly the csproj references by HintPath. Listing them here instead
# of probing the csproj keeps the check readable and lets the failure say
# which one to go find, which is the part a contributor cannot work out.
for name in Assembly-CSharp UnityEngine.CoreModule LogLibrary \
	com.rlabrecque.steamworks.net; do
	path="$MANAGED/$name.dll"
	if [ -f "$path" ]; then
		printf 'ok       %s\n' "$path"
	else
		printf 'missing  %s\n' "$path" >&2
		MISSING=$((MISSING + 1))
	fi
done

if [ -f "$HARMONY" ]; then
	printf 'ok       %s\n' "$HARMONY"
else
	printf 'missing  %s\n' "$HARMONY" >&2
	printf 'ERROR: 0_TFP_Harmony ships with the game; install it in %s/Mods (0_TFP_Harmony is stock, not this repo)\n' \
		"$GAME_ROOT" >&2
	MISSING=$((MISSING + 1))
fi

if ((MISSING > 0)); then
	printf 'ERROR: the client install at %s is missing %d file(s) the mod compiles against\n' \
		"$GAME_ROOT" "$MISSING" >&2
	printf '       point GAME= at the install, or run from a checkout of the game:\n' >&2
	printf '       make build GAME="/path/to/7 Days To Die"\n' >&2
	printf '       `make test` needs no game install; only build/install/package do\n' >&2
	exit 1
fi

echo "game-root: every referenced assembly is present under $GAME_ROOT"
