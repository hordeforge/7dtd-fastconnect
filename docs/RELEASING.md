# Releasing 7dtd-fastconnect

The release is a tag plus one attached zip. Everything that can be checked
automatically is a CI gate; the one step that cannot (compiling against the
shipped client install) is the maintainer's, and this file is that step.

Nothing in a release is built by CI. `make package` compiles against
`$GAME/.../Assembly-CSharp.dll`, which a hosted runner does not have and this
repository does not redistribute, so the archive is produced on a machine with
a client install and attached to the release by hand.

## What ships

One zip, `dist/7dtd-fastconnect-<version>.zip`, holding two files under a
top-level `7dtd-fastconnect/` folder:

- `7dtd-fastconnect.dll`
- `ModInfo.xml`

A sibling `dist/7dtd-fastconnect-<version>.buildinfo` records the commit, the
dirty state, `SOURCE_DATE_EPOCH`, the dotnet SDK version, and the archive's
sha256. It is the record a later rebuild starts from, and it is not part of the
zip. The archive bytes are reproducible: entry mtimes come from
`SOURCE_DATE_EPOCH` (default: the last commit's timestamp), and
`scripts/repro_zip.sh` normalizes order, permission bits, and metadata.

## Prerequisites

- A stock client install with EAC off, at the stock path or wherever `GAME`
  points (`make build` fails with a compile error naming the missing reference
  assembly otherwise).
- `zip` on PATH.
- `uv sync --group dev` for `make test`.

## Steps

1. **Green tree.** `make test` passes on the commit you are about to tag. This
   is the same gate CI runs.

2. **Bump the version in all three declarations.** They must agree or the tag
   gate fails:

   | File | Line |
   |---|---|
   | `ModInfo.xml` | `<Version value="X.Y.Z" />` |
   | `Source/ConnectMod/ModApi.cs` | `public const string Version = "X.Y.Z";` |
   | `pyproject.toml` | `version = "X.Y.Z"` |

   `make gate GATE=scripts/test_version_sync.sh` checks the three without a
   full run. A lagging third declaration is how 0.10.4 shipped under a 0.10.5
   manifest.

3. **Write the notes.** Add a `## [X.Y.Z]` section to `CHANGELOG.md` and
   leave `## [Unreleased]` holding only what is still unreleased. Move the
   entries under the new heading, do not copy them: the notes that ship are
   the ones under the tag's own section.

   Update the link footers at the bottom of the file in the same change:
   `[Unreleased]` must compare from the version being cut
   (`.../compare/vX.Y.Z...HEAD`) and the new `[X.Y.Z]` line must name the
   version. `scripts/changelog_gate.sh X.Y.Z` checks the section, its entries,
   and both links without a tag:

   ```bash
   make gate GATE=scripts/test_changelog_gate.sh   # the gate's own tests
   ./scripts/changelog_gate.sh 0.12.0              # this tree's notes
   ```

4. **Commit, push, tag.**

   ```bash
   git commit -am "release 0.12.0"
   git push origin main
   git tag v0.12.0 && git push origin v0.12.0
   ```

   The tag push runs `.github/workflows/release.yml`, which re-checks the tag
   against `ModInfo.xml`, runs `scripts/test_version_sync.sh`, and runs
   `scripts/changelog_gate.sh` on the tag. A red tag run means a declaration is
   wrong or the notes are missing; fix the file and re-tag, do not push the tag
   past a failing gate.

5. **Build the archive, from the tagged commit.**

   ```bash
   make package
   ```

   The version in the filename comes from the tag HEAD sits on, so run it with
   the tag checked out. A tree with uncommitted tracked changes is refused the
   tag's name: the zip gets a `<commit>-dirty` version instead, which is the
   signal that the artifact would otherwise claim to be a release while
   differing from it. The version must be one filename-safe component
   (digits, letters, `.`, `_`, `-`, starting with a digit or letter); a tag
   carrying a slash, or an override that does, is refused with exit 2 rather
   than writing the archive outside `dist/`.
   `scripts/package.sh --help` documents the overrides
   (`VERSION`, `SOURCE_DATE_EPOCH`).

6. **Verify the artifact before uploading it.**

   ```bash
   unzip -l dist/7dtd-fastconnect-0.12.0.zip   # exactly the two files, one folder
   cat  dist/7dtd-fastconnect-0.12.0.buildinfo
   unzip -o dist/7dtd-fastconnect-0.12.0.zip -d "$GAME/Mods"
   ```

   The zip installs by being unzipped into `$GAME/Mods/`. To prove it in
   place instead, `make install` copies the same two files and needs no
   archive.

7. **Publish.** Create the GitHub release for the tag and attach
   `dist/7dtd-fastconnect-0.12.0.zip` (not the `.buildinfo`; that stays local
   unless you want it recorded). The release badge in the README reads this
   release.

## What CI does and does not do

- `ci.yml` runs on every push and PR: the full gate set (`make test`), the
  workflow and shell lint included. On a push to `main` it also rebuilds the
  coverage badge and publishes it to the `badges` branch.
- `release.yml` runs on a `v*` tag and gates the tag only. It builds nothing.
- There is no branch-protection configuration in this repository, so the
  required-checks list is a repository setting, not a file here. The
  `connect` job is the one to require.

## Rollback

The mod is two files with no server side and no persistent state, so a bad
release rolls back by putting the previous files back:

```bash
# 1. restore the installed copy from the previous release zip
unzip -o dist/7dtd-fastconnect-0.12.0.zip -d "$GAME/Mods"

# or, to remove the mod entirely
make uninstall
```

Removing the mod directory leaves the stock client working: it joins through
the Steam server browser instead, which does not work for a non-Steam server
like zdtd.

A tag that was pushed by mistake is not rewritten here. Cut a new patch
release with the reverted content and the CHANGELOG entry that says so; the
tag gate will not accept a second tag for the same version.
