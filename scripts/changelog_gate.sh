#!/usr/bin/env bash
# Release gate for CHANGELOG.md. The tagged version must have its own notes
# section, that section must carry entries, it must be the newest released
# section, and the compare links at the foot of the file must name the tag.
#
# The tag workflow ran before with a single `grep` for the `## [X.Y.Z]`
# heading, so a version heading with nothing under it passed, and the compare
# links (a hand edit on every release) were checked by nothing. Neither is a
# release blocker on its own; together they are how a release ships with no
# notes a reader can find.
#
# Usage: changelog_gate.sh <version> [changelog]   (version with or without v)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:-}"
CHANGELOG="${2:-$ROOT/CHANGELOG.md}"

if [[ -z "$VERSION" ]]; then
    echo "usage: changelog_gate.sh <version> [changelog]" >&2
    exit 2
fi
VERSION="${VERSION#v}"
if [[ ! -f "$CHANGELOG" ]]; then
    echo "ERROR: no changelog at $CHANGELOG" >&2
    exit 1
fi

fail() {
    echo "ERROR: $*" >&2
    STATUS=1
}
STATUS=0

if ! awk -v want="$VERSION" -v file="$CHANGELOG" '
    /^## \[/ {
        if (in_section) {
            if (entries == 0) {
                printf "ERROR: the `## [%s]` section has no entries; a heading with nothing under it documents nothing\n", want > "/dev/stderr"
                rc = 1
            }
            if (subsections == 0) {
                printf "ERROR: the `## [%s]` section has no `###` subsection; group the notes by impact\n", want > "/dev/stderr"
                rc = 1
            }
        }
        heading = $0
        in_section = (index(heading, "## [" want "]") == 1)
        if (in_section) { found = 1; subsections = 0; entries = 0 }
        name = heading
        sub(/^## \[/, "", name)
        sub(/\].*$/, "", name)
        if (name != "Unreleased" && newest == "") newest = name
        next
    }
    in_section && /^### / { subsections++; next }
    in_section && /^- / { entries++; next }
    END {
        if (!found) {
            printf "ERROR: %s has no `## [%s]` section; write the notes first\n", file, want > "/dev/stderr"
            rc = 1
        }
        if (found && newest != "" && newest != want) {
            printf "ERROR: the newest released section is `## [%s]` but the tag is v%s; the notes for this release were not added\n", newest, want > "/dev/stderr"
            rc = 1
        }
        exit rc
    }
' "$CHANGELOG"; then
    STATUS=1
fi

# The link footers are hand edits on every release: [Unreleased] must compare
# from the tag just cut, and the tag needs its own line to render as a link.
ESCAPED="${VERSION//./\\.}"
if ! grep -qE "^\[Unreleased\]: .*/compare/v${ESCAPED}\.\.\.HEAD$" "$CHANGELOG"; then
    fail "the [Unreleased] link does not compare from v${VERSION}; update the link footers"
fi
if ! grep -qE "^\[${ESCAPED}\]: .*v${ESCAPED}([^0-9.]|$)" "$CHANGELOG"; then
    fail "no '[${VERSION}]:' compare or release link naming v${VERSION}"
fi

if ((STATUS > 0)); then
    exit 1
fi
echo "ok: CHANGELOG.md has notes and links for ${VERSION}"
