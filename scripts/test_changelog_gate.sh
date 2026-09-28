#!/usr/bin/env bash
# Gate for scripts/changelog_gate.sh, the release-time check that the version
# being tagged has real notes and that the compare links name it. Each case
# writes a whole CHANGELOG.md fixture, because the check reads section
# headings, entry bullets, and the link footers as one file.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/test_common.sh"
GATE="$ROOT/scripts/changelog_gate.sh"

SCRATCH="$(scratch_mktemp "$ROOT" test_changelog_gate)"
trap 'rm -rf "$SCRATCH"' EXIT

# A changelog that passes the gate for 0.13.0. The named argument selects a
# mutation: drop_section, empty_section, stale_unreleased_link, no_version_link.
write_changelog() {
	local out="$SCRATCH/CHANGELOG.md"
	local unreleased_link="https://github.com/hordeforge/7dtd-fastconnect/compare/v0.13.0...HEAD"
	local version_link="https://github.com/hordeforge/7dtd-fastconnect/compare/v0.12.0...v0.13.0"
	local section="## [0.13.0] - 2026-10-01

### Fixed

- A released fix.
"
	case "${1:-}" in
		drop_section) section="" ;;
		empty_section) section="## [0.13.0] - 2026-10-01
" ;;
		stale_unreleased_link) unreleased_link="https://github.com/hordeforge/7dtd-fastconnect/compare/v0.12.0...HEAD" ;;
		no_version_link) version_link="" ;;
		no_subsection) section="## [0.13.0] - 2026-10-01

- A released fix with no impact group.
" ;;
	esac
	{
		printf '# Changelog\n\n## [Unreleased]\n\n### Fixed\n\n- Unreleased work.\n\n'
		printf '%s' "$section"
		printf '## [0.12.0] - 2026-09-11\n\n### Fixed\n\n- The previous release.\n\n'
		printf '[Unreleased]: %s\n' "$unreleased_link"
		printf '[0.13.0]: %s\n' "$version_link"
		printf '[0.12.0]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.11.0...v0.12.0\n'
	} >"$out"
	printf '%s' "$out"
}

check() {
	local name="$1" mutation="$2" version="$3" expected="$4"
	local changelog
	changelog="$(write_changelog "$mutation")"
	if "$GATE" "$version" "$changelog" >/dev/null 2>&1; then
		local got=pass
	else
		local got=fail
	fi
	if [[ "$got" == "$expected" ]]; then
		echo "PASS $name"
	else
		echo "FAIL $name: expected $expected, got $got" >&2
		FAILS=$((FAILS + 1))
	fi
}

assert "the shipped changelog has notes and links for 0.12.0" \
	"$GATE" 0.12.0

check "accepts a complete section" "" 0.13.0 pass
check "accepts a version with a leading v" "" v0.13.0 pass
check "rejects a missing section" drop_section 0.13.0 fail
check "rejects a section with no entries" empty_section 0.13.0 fail
check "rejects a section with no impact group" no_subsection 0.13.0 fail
check "rejects a stale [Unreleased] compare link" stale_unreleased_link 0.13.0 fail
check "rejects a missing [version] link" no_version_link 0.13.0 fail
check "rejects a version older than the newest section" "" 0.12.0 fail
check "rejects a version that never existed" "" 0.10.1 fail

assert "rejects a missing version argument" \
	bash -c "! '$GATE' >/dev/null 2>&1"
assert "rejects a changelog that is not there" \
	bash -c "! '$GATE' 0.13.0 '$SCRATCH/absent.md' >/dev/null 2>&1"

finish
