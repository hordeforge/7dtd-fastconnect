# Changelog

Notable changes to this project are documented in this file. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); each section
matches a release tag, and the mod version is the one `ModInfo.xml` shipped.

Versioning is inferred from history (no policy was ever published): this is a
0.x project where minor bumps (`0.Y.z`) may change behavior and patch bumps are
expected not to. History has not always met that bar; deviations are called out
in the affected sections instead of being papered over.

## [Unreleased]

### Changed

- Renamed the project from **7dtd-connect** to **7dtd-fastconnect**
  (`ae212bc`); install path is now `<game>/Mods/7dtd-fastconnect/`.

### Added

- `GFX_API` launcher variable to select the graphics backend (`cb5d26c`,
  #19). `d3d11` remains the default, so existing launches are unchanged.
- `unmute_client_audio.sh` next to the mute helper (#16).
- In-world frame-hitch monitor under `diag on`, with docs for reading hitches
  and the platform-identity trap (#13, #14, #15).
- C# line-coverage badge lane in CI (#17).
- Release gate: a pushed `vX.Y.Z` tag must match the version `ModInfo.xml`
  ships, or the release workflow fails (#21).
- uv-managed Python dev tooling backing the offline gates.

### Added

- Byte-reproducible packaging: `scripts/repro_zip.sh` normalizes zip entry
  mtimes (`SOURCE_DATE_EPOCH`, defaulting to the last commit's timestamp),
  sorts entries explicitly, pins `TZ=UTC`/`LC_ALL=C`, and strips
  uid/gid/extra fields, so two builds of one tree produce identical archive
  bytes. Pinned by `scripts/test_repro_zip.sh` in `make test`.
- dotnet SDK band pinned by `global.json` (8.0.x, matching the CI coverage
  lane); CI installs it via `actions/setup-dotnet` reading that file instead
  of a separate version input.
- The C# parse-test lane now falls back to the dotnet SDK when mono `mcs` is
  absent (same harness project as the coverage lane), so CI runners without
  mono run the behavioral tests instead of skipping them.

### Changed

- `make package` refuses to name a dirty-worktree artifact after the release
  tag: uncommitted tracked changes fall back to `<shortsha>-dirty`.
- The Makefile `DOTNET_ROOT` heuristic only honors candidate roots that
  actually contain an SDK (`sdk/` subdir), instead of exporting a broken
  `DOTNET_ROOT`/`PATH` that breaks SDK resolution.

### Fixed

- Lifecycle-script hardening: surfaced silent probe failures, fail-fast on
  missing binaries, monotonic marker-scan resumes, log-dir creation before
  truncation, signal forwarding from launcher to game child.
- Auto-join idle state no longer reported as an unset target.

## [0.10.5] - 2026-08-23

**No code changes; mislabeled duplicate of 0.10.4.**

This tag points at the same commit as [0.10.4] (`5ab0efd`), whose manifest
still reads `0.10.4`. Because `scripts/package.sh` names the zip after the tag
but the packaged `ModInfo.xml`/mod code carry their own constant, the
distributed `7dtd-fastconnect-0.10.5.zip` contains a mod that identifies as
**0.10.4**. If you installed "0.10.5", you have exactly the 0.10.4 build; there
is nothing extra to upgrade to until the next real release.

## [0.10.4] - 2026-08-23

### Added

- Automation boot mode isolation (#6): patches that skip news/EULA/Discord and
  drive auto-join are enabled only when `7DTD_CONNECT` or `-connect=` supplies
  a launch target, or explicitly via `7DTD_CONNECT_AUTOMATION=1`. Regular
  client launches keep stock login, menu, EULA, Discord, and loading behavior.
- `7DTD_CONNECT_FORCE_LOAD_SYNC=0` opt-out from the synchronous-load override
  used by automation boot mode (#5).
- Bracketed IPv6 hosts in connect targets (`connect [::1]:27025`).
- .NET analyzers with warnings as errors on the mod build.

### Fixed

- Local-platform world load no longer stalls under Proton: pending async
  addressable loads are drained before local player creation, with sync
  loading held until server start completes.
- Connect-ready gate polled at 10 Hz instead of per frame.
- EULA-block guard keyed by window name before log-tag concatenation;
  swallowed errors in lifecycle scripts are logged instead of hidden.

## [0.10.3] - 2026-08-22

### Breaking

- **Removed the legacy `ZDTD_*` environment aliases** with no fallback shim or
  grace period. This is an env-contract break, and it shipped in a patch-level
  bump (0.10.2 -> 0.10.3) rather than a minor bump; recorded here so the break
  is findable, since published tags cannot be renumbered. Migration:

  | Removed alias            | Use instead                 |
  |--------------------------|-----------------------------|
  | `ZDTD_CONNECT`           | `7DTD_CONNECT`              |
  | `ZDTD_CONNECT_DEBUG`     | `7DTD_CONNECT_DEBUG`        |
  | `ZDTD_PLAYER_NAME`       | `7DTD_PLAYER_NAME`          |
  | `ZDTD_DUMP_BLOCK_IDS`    | `7DTD_DUMP_BLOCK_IDS`       |
  | `ZDTD_DUMP_BLOCK_IDS_PATH` | `7DTD_DUMP_BLOCK_IDS_PATH` |
  | `ZDTD_DUMP_ENTITY_CLASS` | `7DTD_DUMP_ENTITY_CLASS`    |

  Launch scripts must pass these through `env` because bash cannot export
  names starting with a digit.

## [0.10.2] - 2026-08-22

First tagged release; earlier 0.9.x history carries no tags. The version
jumped straight from 0.9.5 to 0.10.2: releases 0.10.0 and 0.10.1 do not exist.

### Added

- Renamed the mod from **zdtd-connect** to **7dtd-connect** (later renamed
  again to 7dtd-fastconnect, see Unreleased).
- Steamless LAN join for non-Steam servers: synthetic host-derived ID,
  EULA-gate handling, distinct local test-player names.
- `CLIENT_PLATFORM=local` no-Steam mode: swaps `platform.cfg` to the Local
  platform and restores it on exit.
- Client audio muted by default at the OS layer on launch (`CLIENT_MUTE=0`
  keeps sound).
- F1 console `diag on/off/toggle/status`; spammy traces gated behind
  `7DTD_CONNECT_DEBUG=1`.
- Proton prefix derived from `GAME`, overridable Steam paths and Mods dir.

[Unreleased]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.10.5...HEAD
[0.10.5]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.10.4...v0.10.5
[0.10.4]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.10.3...v0.10.4
[0.10.3]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.10.2...v0.10.3
[0.10.2]: https://github.com/hordeforge/7dtd-fastconnect/releases/tag/v0.10.2
