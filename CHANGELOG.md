# Changelog

Notable changes to this project are documented in this file. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); each section
matches a release tag, and the mod version is the one `ModInfo.xml` shipped.

Versioning is inferred from history (no policy was ever published): this is a
0.x project where minor bumps (`0.Y.z`) may change behavior and patch bumps are
expected not to. History has not always met that bar; deviations are called out
in the affected sections instead of being papered over.

## [Unreleased]

### Fixed

- `zero_nre_join_loop.sh` stops the server it started by pid and reaps it,
  instead of only sweeping processes named `zdtd`. A server launched through
  the `ZDTD_BIN` override whose binary is not named `zdtd` was invisible to
  the sweep, so the run left it ticking its world and holding the port, and
  the next run waited on that stale listener.

### Added

- `make test` lints the workflow YAML with `yamllint`, configured by the new
  `.yamllint` (100-column cap, bare `on:` trigger key, no document marker).
- `restart_pair.sh` takes `HOST` (default `127.0.0.1`) for the join target it
  hands the client through `7DTD_CONNECT`, the knob the other two harnesses
  already honour and the README lists for them. A value carrying whitespace is
  a usage error (exit 2), checked before anything is torn down.
- A boolean env var holding an undocumented token now logs a warning naming
  the variable and the value (`7DTD_CONNECT_DEBUG=ture`). The reading is
  unchanged (unknown means on); the log just stops looking like a deliberate
  setting. `1` / `true` / `yes` / `on` are now documented opt-in tokens.
- `PORT` is checked against the real TCP range (1-65535) by the three join
  harnesses, through the shared `scripts/config_validate.sh`. A value the
  client could never join is reported at startup instead of surfacing later as
  a listen or join timeout; `restart_pair.sh` calls it a usage error (exit 2).

### Changed

- The scripts that take no positional arguments (`package.sh`,
  `one_shot_join.sh`, `zero_nre_join_loop.sh`, `unmute_client_audio.sh`, and a
  second argument to `mute_client_audio.sh`) now exit 2 with the usage on
  stderr instead of ignoring the argument, so a mistyped flag cannot start a
  client, a server, or a multi-minute build. The README states the shared
  contract and `test_cli_help.sh` pins it.
- `mute_client_audio.sh` names where a bad wait value came from (the
  `wait-seconds` argument or `CLIENT_MUTE_TIMEOUT`) instead of always pointing
  at the env var, and its help lists the statuses it can exit with.
- `coverage_badge.py` answers `-h` / `--help` on stdout, and every entry point
  reports a usage error as `usage: <name> ...` on stderr, the way the shell
  scripts do.
- ruff now runs the `S`, `SIM`, `EM`, `COM818/819`, `ISC`, `Q`, `TID`, `TCH`
  and `ERA` groups alongside the existing ones, with the assert and
  partial-path subprocess rules scoped off for `scripts/test_*.py` and the XML
  parse rule for the Cobertura file the coverage lane just generated.
- The ruff and mypy gates in `make test` are mandatory: without `uv` or
  `ruff`+`mypy` on PATH the run fails instead of printing a warning and
  reporting a green that analyzed nothing.
- F1 console replies are actionable. `connect` echoes the argument it read and
  the reason names the fix: the port range for a bad port, the bracketed IPv6
  form for an unclosed `[`, the expected `host[:port]` shape for a missing
  host, instead of `bad port` / `empty host`. `diag` rejects an unknown
  argument by name and still prints the current state, rather than silently
  answering as if it were `status`. A pasted port is truncated in the echo so
  the reason stays on screen.

- The log-marker scanner skips the grep when the join log has not grown since
  its last scan of that marker. The window is byte-identical, so the verdict
  cannot differ, and the join log is bursty: most polls used to re-run `tail`
  plus `grep` over an unchanged tail for the whole join budget. External
  truncation (size below the offset) still falls back to a full scan, and a
  poll still forks once whenever the log grew.

- The player name applied at boot is no longer echoed to the client log. The
  line records only whether the name came from `7DTD_PLAYER_NAME` or from the
  `PlayerNames` fallback; the value itself (an OS account name, host name, or
  operator-supplied label) no longer lands in a log that gets pasted into bug
  reports.
- `launch_client.sh` trims and case-folds every enum value (`GFX_API`,
  `CLIENT_PLATFORM`, `CLIENT_MUTE`), so `GFX_API=Vulkan` and `GFX_API=" vulkan "`
  select the documented backend instead of aborting as usage errors. Unknown
  values still abort.
- `CLIENT_MUTE_TIMEOUT` is validated by the launcher that announces the poll
  window, so a bad value names itself in the launch log.
- Every env read in the mod goes through one guarded helper. An environment
  that cannot be read now falls back to the default instead of throwing out of
  a static initializer while the mod loads.

### Fixed

- Probe failures are announced once per probe, not once per process. A single
  shared latch let the first throwing heartbeat mute every later notice,
  including the synthetic platform id, which is the one failure that silently
  changes the server-side player identity.
- The pending-async-load counter announces a reflection failure once instead of
  on every call. Both callers poll it per frame, so a game update that renames
  a `LoadManager` field flooded the client log.
- `connect <host> <port>` no longer discards the port argument in silence when
  the host already carries one: the dropped token is named in the log, since
  the join lands on the other port.
- `make package` ships only the mod payload. The zip was built by copying the
  whole `dist/7dtd-fastconnect` build output, which MSBuild never prunes, so
  the release archive could carry `7dtd-fastconnect.pdb` and files a build had
  renamed or dropped. `scripts/stage_mod.sh` now copies the two files
  `make install` installs and fails if the build did not produce them, so a
  partial build cannot be zipped under a release name.
- Concurrent `launch_client.sh` runs no longer corrupt each other's
  `platform.cfg` swap. The backup file is a single slot, so two launchers on
  one install each backed up the other's config and left `platform.cfg` stuck
  on Local with the Steam original lost. A launcher now holds an exclusive
  `flock` on the install for as long as it owns the swap, takes no second
  backup when another live launcher already holds it, and restores only a
  backup it created itself. A missing or unusable `flock` degrades to the old
  unlocked behavior with a warning rather than refusing to launch. Single
  launcher launches, including the hard-kill self-heal, are unchanged.
- The cross-platform `PlatformUserId` wait window is scoped to one contiguous
  episode. The deadline was reset only when a user object was seen, so a
  platform or user torn down in between left the previous deadline armed; the
  next wait was already past due, skipped its whole window, and joined into
  the null-reference the wait exists to avoid. The reset now also runs when
  the platform is gone or was never cross-platform.

- `launch_client.sh` trims the client-mute opt-out before matching it, so
  `CLIENT_MUTE=" 0"` (or `"OFF "`, `" No"`) is the opt-out the README documents
  instead of falling through to "any other non-empty value" and muting the
  session anyway. The mod's `EnvFlags` twin and the `CLIENT_PLATFORM` gate in
  the same script already trimmed.
- Invisible Unicode format characters (bidi overrides and embeddings, LRM/RLM,
  the zero-width joiners, the BOM) are flattened in the log-sanitizing twins
  (`ConnectTarget.SanitizeForLog` and `scripts/log_sanitize.sh`). They are not
  control characters, so they survived both sanitizers: a terminal renders
  nothing for them while `grep` still matches the bytes, which let a crafted
  `7DTD_CONNECT` or `7DTD_PLAYER_NAME` value produce a line that reads back
  differently from what the join harnesses grep for.
- `7DTD_PLAYER_NAME` is normalized before it is stored in `GamePrefs`, not
  only before it is echoed to the log. The name is sent to the server, so a
  newline or bidi override in it could forge a line in a server log. The
  length cap no longer truncates between the halves of a surrogate pair.
- The CI badge job authenticates with an `http.extraheader` instead of a token
  embedded in the git remote URL, which wrote `GITHUB_TOKEN` into `.git/config`
  on the runner and into the process table for every git command.

## [0.12.0] - 2026-09-11

### Fixed

- `launch_client.sh` now passes `-disablenativeinput`. V 3.2.0 enables
  InControl `NativeInputDeviceManager` by default, which crashes Proton
  before mods load.
- Proton `WINEDLLOVERRIDES` disables `xinput1_3` / `xinput1_4` / `xinput9_1_0`.
  V 3.2 InControl still calls `XInputGetState` after `-disablenativeinput`;
  Proton's xinput stub hard-crashes a Steam-free Local client there.

## [0.11.0] - 2026-08-26

Scope enforcement release: the mod is now only join and automation plumbing,
as AGENTS.md rules 3-5 always required. Three features are gone. Nothing that
connects, auto-joins, or skips a boot gate changed.

### Removed

- **Tab bot-list injector** (`BotTabPatch.cs`). It synthesized
  `PersistentPlayerData` rows for `[Bot]`-named server entities and injected
  them into `XUiC_PlayersList` by reflection. That is gameplay UI built from
  client-invented state for players the server never sent, which rules 3 and 4
  forbid; it was also undocumented and untested. A dedicated server that wants
  bots in Tab sends them as players.
- **Block-id and entity-class RE dumpers** (`BlockIdDump.cs`,
  `EntityClassDump.cs`) with env `7DTD_DUMP_BLOCK_IDS`,
  `7DTD_DUMP_BLOCK_IDS_PATH`, `7DTD_DUMP_ENTITY_CLASS`. Stock-game reverse
  engineering belongs in `../7dtd-engine-research/`, not in the client mod.
  This also closes threat-model **R1**: `7DTD_DUMP_BLOCK_IDS_PATH` was used
  verbatim as a `File.WriteAllText` destination, so launch-env control meant
  arbitrary file overwrite with client privileges. There is no longer a write
  path to disable.
- **Terrain forensics from the spawn heartbeat**: the block column, density
  channel, collision-mesh raycast, chunk neighbour ring, chunk-cache window,
  and the Navezgane `abandoned_house_07` POI probe hardcoded to world
  coordinates, plus the in-game screenshot writer. These instrumented a
  server-side chunk-delivery gap that rule 5 moved to zdtd. The heartbeat
  keeps its join-gate probes: load gate, movement replication, respawn UI,
  and open windows.
- `UserDirs.cs` and the `UnityEngine.PhysicsModule` /
  `UnityEngine.ScreenCaptureModule` assembly references, dead once the above
  were gone.

### Changed

- Renamed the project from **7dtd-connect** to **7dtd-fastconnect**
  (`ae212bc`); install path is now `<game>/Mods/7dtd-fastconnect/`.
- `make package` refuses to name a dirty-worktree artifact after the release
  tag: uncommitted tracked changes fall back to `<shortsha>-dirty`.
- The Makefile `DOTNET_ROOT` heuristic only honors candidate roots that
  actually contain an SDK (`sdk/` subdir), instead of exporting a broken
  `DOTNET_ROOT`/`PATH` that breaks SDK resolution.
- Offline gates and the coverage lane stage their temporaries under the
  repo's gitignored `.scratch/` instead of `$TMPDIR`/`/tmp`, which is tmpfs:
  staged game trees and zip fixtures were charged to RAM. pytest's
  `tmp_path` moves with them via `--basetemp`.
- `make test` type-checks every `scripts/*.py` rather than one named file,
  and gates formatting with `ruff format --check`.
- Thresholds and placeholders that were literals are named constants: the
  heartbeat interval (shared by the boot, spawn, and load probes), the hitch
  threshold, the load-gate start-bar slack, the port range, the FNV-1a
  parameters and synthetic-id band, and the `GameServerInfo` fields the stock
  direct-connect path leaves unset.
- Every empty `catch` states what it swallows and why nothing downstream can
  act on it; the diagnostic traces that silently dropped their own failures
  now announce the first one through `ProbeFailure`.

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

### Fixed

- Lifecycle-script hardening: surfaced silent probe failures, fail-fast on
  missing binaries, monotonic marker-scan resumes, log-dir creation before
  truncation, signal forwarding from launcher to game child.
- Auto-join idle state no longer reported as an unset target.
- `test_launch_client_platform.py` finds the repo root by walking up to
  `pyproject.toml` instead of counting parent directories, which broke
  silently if the file moved and surfaced as a missing launcher.

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
  again to 7dtd-fastconnect, see 0.11.0).
- Steamless LAN join for non-Steam servers: synthetic host-derived ID,
  EULA-gate handling, distinct local test-player names.
- `CLIENT_PLATFORM=local` no-Steam mode: swaps `platform.cfg` to the Local
  platform and restores it on exit.
- Client audio muted by default at the OS layer on launch (`CLIENT_MUTE=0`
  keeps sound).
- F1 console `diag on/off/toggle/status`; spammy traces gated behind
  `7DTD_CONNECT_DEBUG=1`.
- Proton prefix derived from `GAME`, overridable Steam paths and Mods dir.

[Unreleased]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.12.0...HEAD
[0.12.0]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.11.0...v0.12.0
[0.11.0]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.10.5...v0.11.0
[0.10.5]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.10.4...v0.10.5
[0.10.4]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.10.3...v0.10.4
[0.10.3]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.10.2...v0.10.3
[0.10.2]: https://github.com/hordeforge/7dtd-fastconnect/releases/tag/v0.10.2
