# Changelog

Notable changes to this project are documented in this file. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); each section
matches a release tag, and the mod version is the one `ModInfo.xml` shipped.

Versioning is inferred from history (no policy was ever published): this is a
0.x project where minor bumps (`0.Y.z`) may change behavior and patch bumps are
expected not to. History has not always met that bar; deviations are called out
in the affected sections instead of being papered over.

## [Unreleased]

## [0.13.0] - 2026-10-01

This is a minor release: the notes below add launcher
configuration validation and a `HOST` knob, and change the launcher's argument
and enum handling, so a patch bump would claim none of that.

### Breaking

- The scripts that take no positional arguments (`package.sh`,
  `one_shot_join.sh`, `zero_nre_join_loop.sh`, `unmute_client_audio.sh`, and a
  second argument to `mute_client_audio.sh`) exit 2 with the usage on stderr
  where they used to ignore the argument and run. A wrapper that passed a stray
  path or flag positionally, and watched it be discarded, now fails the
  invocation; drop the argument. A mistyped flag can no longer start a client,
  a server, or a multi-minute build. The README states the shared contract and
  `test_cli_help.sh` pins it.
- `PORT` outside 1-65535 is a usage error (exit 2) in `restart_pair.sh`, which
  used to run to a listen or join timeout on a value the client could never
  join. `one_shot_join.sh` and `zero_nre_join_loop.sh` keep the fallback they
  always had: a warning naming the value, then the default port.
- A port in `7DTD_CONNECT`, `-connect=` or `connect <host> <port>` is read as
  decimal digits only. `+80`, ` 80`, `1,234` and non-ASCII digits are now the
  rejection they are in the launcher's `PORT` check, which runs under
  `LC_ALL=C`; the client's parse was culture-dependent, so a value the harness
  would have refused was joined.

### Added

- The F1 `connect` command now reports how the attempt ended. It said only
  `connecting to <ip:port>`, and a join that never landed was indistinguishable
  from a slow one until the 30 s window refused the retry with a message
  naming a connect that was no longer running. A watched request now answers
  once, `connected to <ip:port>` or the window closing with nothing behind it
  and what to check next.
- `docs/PRIVACY.md` maps the personal data the mod touches: the player display
  name (source, the pref it is stored in, the server it reaches, and the fact
  that it never reaches the client log), the synthetic platform id the
  Steam-less path sends, and the harness artifacts with their pruning. It also
  states the erasure path, since a stored name is stock preference state a
  user may want cleared. `test_player_name_override.sh` pins the claims that
  name this code, so the page cannot drift from the flow it describes.
- `ruff` selects the correctness groups the tree already passes: `BLE`,
  `TRY`, `C90`, `N`, `PIE`, `FLY`, `G`, `LOG`, `SLF`, `ASYNC`, `FA`, `TD`,
  `FIX` and `ANN`. A blind except or a swallowed error had no enabled rule
  naming it. `COM` stays at `COM818`/`COM819` because `COM812` contradicts
  `ruff format`, and the two would rewrite the same line.
- The release zip and `make install` now ship `LICENSE` alongside the
  assembly and the manifest. The mod is redistributed as a zip, so its
  terms have to travel with the payload.


- `make help` lists the targets, and `make gate GATE=scripts/test_<name>.sh`
  runs a single shell gate. Checking one script used to mean running all
  twenty.
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
  harnesses, through the shared `scripts/config_validate.sh`, so a value the
  client could never join is reported at startup instead of surfacing later as
  a listen or join timeout; `restart_pair.sh` calls it a usage error (exit 2).
- `START_SERVER` reads the documented boolean table in `one_shot_join.sh`. It
  was compared to `1`, so `START_SERVER=true` read as off and the cycle failed
  much later as "no listener on PORT", naming the port instead of the knob.
- `env_bool` in `scripts/config_validate.sh`, the shell twin of the mod's
  `EnvFlags`, now also reads `CLIENT_MUTE`. An undocumented token there was
  coerced to mute in silence, so `CLIENT_MUTE=ture` looked like a deliberate
  setting while the mod's own `7DTD_CONNECT_*` flags warned about the same
  mistake.
- The join harnesses' `--help` lists the knobs they read but did not document
  (`ZDTD_BIN`, `WORLD_DIR`, `MAP_DIR`, `GAME_DIR`, and the `GAME` / `COMPAT` /
  `STEAM_ROOT` / `STEAM_APPID` prefix resolution).
- A pushed `vX.Y.Z` tag runs `scripts/test_version_sync.sh` and
  `scripts/changelog_gate.sh` before it is accepted. The tag gate previously
  matched the version against `ModInfo.xml` alone, so a tag could ship with a
  `ModApi.cs` or `pyproject.toml` still reading the previous version (the drift
  that shipped 0.10.5 pointing at a 0.10.4 build), and it accepted a
  `## [X.Y.Z]` heading with no notes under it. `changelog_gate.sh` also
  requires the compare links at the foot of the file to name the tagged
  version, which is a hand edit nothing else checks.

### Changed

- The NuGet lockfile is now enforced by the project, not only by `make build`:
  `Source/ConnectMod/ConnectMod.csproj` sets `RestoreLockedMode`, so an IDE or
  a hand-run `dotnet build` fails instead of resolving freely and rewriting
  `packages.lock.json` in the source tree. Re-locking adds
  `-p:RestoreLockedMode=false` to the documented `dotnet restore` command.
- Every `dotnet` call from make runs with telemetry, the first-run banner, and
  the CLI message language pinned, so a build sends nothing off-host, writes
  no machine-local state, and does not print in the caller's locale.
- `docs/RELEASING.md` gained the rebuild-and-compare step: package the same
  commit from a second checkout and diff the two zips, so a reproducible
  archive is verified rather than assumed.
- A rejected `connect` target echoed what the console read inside parentheses,
  which the port reason already used for the offending half, so the line read
  `port must be a number from 1 to 65535 (got 'notaport') (got
  '127.0.0.1:notaport')`. The outer echo is now labeled `typed '...'`. A token
  the grammar has no place for is also logged as a failure rather than a note,
  matching every other thing a player typed that did not happen.
- Four copies of one thing became one, with no behaviour change.
  `EnvFlags` spelled the boolean token table twice, once per direction, behind
  four predicates; it now has a single `Parse` over one table, and the opt-in
  and opt-out readers are one comparison each. The two tables could document
  a token on one side only, and the fuzz lane in `test_connect_target_parse.sh`
  now reads the same table the mod does.
  `World.LoadWorld` held `LoadManager.forceLoadSync` through its own copy of
  the reflection, its own saved value and its own restore, next to the
  hold/release pair `createWorld` used; both now go through that pair, which is
  the only latch on the field.
  `DiagToggle` kept the console override as a bool beside a separate "did the
  command ever run" flag, which is a nullable bool spelled as two fields.
  `ProbeFailure.Announce` and the two single-use error-message helpers in
  `ConnectTarget` had one caller each and are now inline.
- The local-host startup trace prefix is `startup trace:` and each step line
  names its stage (`StartAsServer -> step ...` / `createWorld step ...`).
  The old prefix said `StartAsServer trace:` on createWorld lines too, so a
  reader could not tell which stage a line came from. Nothing greps that
  prefix.
- `CLIENT_MUTE` (and its `SEVEN_DAYS_TO_DIE_CLIENT_MUTE` alias) warns on an
  undocumented value and mutes, instead of reading it as a plain opt-in. The
  README's rule was that no documented boolean is silently accepted; the
  launcher did that while `CLIENT_PLATFORM` right below it warned.
- The launcher's and the join harnesses' rejection messages run the rejected
  value through `sanitize_log_text`, so a newline or bidi override in
  `CLIENT_MUTE_TIMEOUT`, `MUTE_POLL_STOP_GRACE_SEC`, `PORT`, `TIMEOUT_SEC`,
  `SETTLE_SEC`, `CYCLE` and `MAX_ATTEMPTS` cannot forge a second log line.
  `sanitize_log_text` no longer needs `tr`, so it also works in the
  stripped-PATH runs the degradation gates use.
- `diag` help lists the argument spellings `Execute` accepts (`1`, `0`,
  `enable`, `disable`, `true`, `false`, `flip`) and that a bare `diag` is
  `status`.
- `make test` fails when `shellcheck` is missing instead of printing a WARN
  and continuing. A run that reported green had linted no shell source at
  all. `yamllint`, `ruff` and `mypy` already failed loud for the same reason;
  the README no longer claims the two shell/YAML gates are advisory.
- The C# offline gate compiles under the same analysis posture as the
  shipping project: the `mcs` lane builds at `-warn:4 -warnaserror+`, and
  the generated harness csproj sets `EnableNETAnalyzers`,
  `AnalysisLevel=latest` and `TreatWarningsAsErrors`. Both lanes previously
  printed compiler and analyzer warnings and passed anyway, so the nine
  production sources this gate compiles reached CI unanalyzed.
- The pinned Python gates install through `--locked` instead of `--frozen`,
  and `pyproject.toml` names pypi.org as the index they resolve from.
  `--frozen` only promises not to rewrite `uv.lock`, so a pin edited in
  `pyproject.toml` without a re-lock kept installing the versions the lock
  still named, with nothing failing; `--locked` fails that run the way the
  C# build already fails a drift. The named index is the same reason
  `NuGet.config` clears the inherited sources: an inherited mirror could
  otherwise answer for these package ids, and the per-artifact sha256 in
  `uv.lock` is what makes the install verifiable. `required-version` refuses a
  uv too old to read the lock, which would have to re-lock to use it.
  `test_make_tool_pin.sh` holds all three in place.


- The local-host startup trace no longer builds its line when verbose tracing
  is off. `PerfTrace.Trace` gates the log write, but the caller concatenated
  the line first, so every step of a world load allocated a string that was
  dropped. The call sites now test `DiagToggle.Enabled` before building one;
  the trace output is unchanged when diag is on.
- A rejected target or a failed connect from the F1 console is logged at error
  severity, matching what the auto-join path already did for the same outcome.
  The console still shows the message; only the log level changed, so a
  client-log-only read no longer shows a clean run for a join that never
  happened.
- The connect-wait lines carry the seconds the gate held (`t=`) next to the
  poll count, so a hung join is read from how long it waited rather than how
  often it polled.
- Every caught exception logged by the mod names its type next to the message
  (`NullReferenceException: ...`), so a one-word message can be told apart
  from a differently-shaped failure at the same call site. The `IsReady` gate
  reason the join-wait line echoes uses the same shape.
- The two `IsReady` expiry notes (a platform identity that never arrived, so
  the join proceeds anyway) are logged at warning severity, since the join
  they precede is the one that fails authentication.
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
- The log-line hygiene helpers moved out of `ConnectTarget` into their own
  leaf module, `LogText`. `EnvFlags` (a low-level env reader) needed
  `ConnectTarget.SanitizeForLog` to flatten a bad env value, so the leaf
  depended on the join module above it; the two now meet at `LogText`, which
  depends on neither. Behavior is unchanged and the same offline gates cover
  it.
- The local-host frame-hitch monitor and startup step trace moved from
  `LocalHostWorldLoadPatches.cs` into `PerfTrace.cs`. They are diagnostics, not
  part of the world-load workaround, and the other opt-in probes already live
  in their own `*Trace.cs` files.
- The `[7dtd-fastconnect] ` log prefix is the constant `LogText.Tag` rather
  than a literal at every call site, so the marker the join harness greps
  for has one definition. Emitted lines are unchanged.
- `docs/architecture.md` maps every file under `Source/ConnectMod/` and
  `scripts/` to what it owns, and names the dependency direction inside the
  mod, which was only discoverable by reading every source.

### Fixed

- Display names remain trimmed when the length cap exposes a space at the end, including the .NET test lane used by CI.

- The changelog test reads the shipped version from ModInfo.xml, so a release bump does not leave the gate checking the previous release.
- A DNS lookup that outlives the 5 s connect wait no longer throws
  `ObjectDisposedException` on the resolver thread: the wait handle is closed
  only once the lookup has completed, and an abandoned one keeps its handle.
- The game and the mute poller that `launch_client.sh` starts no longer
  inherit the `platform.cfg` swap lock. They held it for as long as they ran,
  so the next launcher was refused the swap after the one that took it had
  exited and restored the file.
- Every Unicode format character (Cf) is now flattened out of a value on its
  way to a log line, in both `LogText.SanitizeForLog` and the shell twin
  `sanitize_log_text`. The set was a hand-kept list of the bidi controls, the
  zero-width joiners and the BOM, so U+00AD, U+061C, U+FFF9..U+FFFB,
  U+206A..U+206F, the Egyptian hieroglyph format controls, the shorthand and
  musical format controls, and the whole U+E0001 tag block reached the log
  untouched.
  A value ending in a tag run reads as the shorter plain one on a terminal and
  still carries the bytes a harness greps. The C# walk also moved from `char`
  to code point, because the tag and Egyptian blocks are above the BMP and a
  per-char test only ever saw their surrogate halves. Both sides list the whole
  of Cf as Unicode 15.1 defines it rather than asking
  `CharUnicodeInfo.GetUnicodeCategory`, whose answer is the runtime's Unicode
  database: the game's Mono predates several of these blocks, so a query there
  would have passed all of them through.
- The player-name cap no longer leaves half an emoji behind. The cut was
  code-point-exact and surrogate-safe, but 24 code points is not a character
  boundary: a name whose family emoji, flag, skin-tone modifier or accented
  cluster straddled the cap was stored with a dangling ZWJ, a lone regional
  indicator or a base-less combining mark, and rendered as a box on the
  server's player list.
- `coverage_badge.py` reported 0% coverage, and exited 0, for a report no line
  of which matched the filter. A profiler run that collected nothing therefore
  published a measurement. It now fails, naming the filter, and writes no
  badge; `scripts/test_coverage_badge.sh` pins both the rate and the refusal.
- A `World.LoadWorld` hold on `LoadManager.forceLoadSync` restored the field
  from an unguarded `finally`, where a throwing `SetValue` escaped into stock
  game code; it is now guarded and logged, like the hold and release beside it.
  The local-host player-prefab prewarm also read `IsDone` on a request
  `LoadManager` can decline to return at all, ending the coroutine with an
  NRE Unity reports nowhere; that is named and skipped instead.
- The `InitMod` boot-unblock warning covered the frame uncap and the
  force-sync set together and dropped the exception type, so a log line could
  not say which setting did not apply. The two are caught and reported
  separately, in the type-and-message shape every other catch in the mod uses.
- `coverage-cs.sh` reported a missing build output as a per-mode profiler
  failure naming the profiler; it now fails on the missing `cov.dll` itself.
- Reruns of the packaging path destroyed the artifact a previous run
  produced. `repro_zip.sh` deleted the archive at the release path before
  zipping a replacement, so a run that failed (no `zip`, full disk, killed)
  left nothing where a good release zip had been; the delete was also
  unnecessary, since nothing was ever zipped into that path. `package.sh` now
  builds the archive at a per-run candidate path, verifies it there, and only
  then renames it onto the release path, so the path a release is published at
  only ever holds an archive this run verified. Both repack to identical bytes
  on a second run, and `test_repro_zip.sh` and `test_package_verify.sh` pin
  the rerun property. A killed run's stage and candidate paths are pruned
  instead of accumulating under `dist/`.
- `zero_nre_join_loop.sh` left `zero_nre_PASS.txt` from an earlier run on disk
  when a later run failed, so a pass and a fail sat side by side with nothing
  to say which run each came from. Both verdict files are cleared at the start
  of a run, pinned by `test_zero_nre_verdict_reset.sh`.
- An F1 `connect` that reached the server held the one-attempt-at-a-time latch
  until its 30 s window expired, because only the auto-join poll (which the
  console command has no counterpart of) reported the outcome. Leaving the
  session and joining again inside that window was refused as a duplicate.
  The connect path now records the outcome from the live connection it sees,
  whoever started the attempt.
- `restart_pair.sh` swept the previous server with `pkill -f "$ZDTD"`, an
  unescaped extended regular expression matched against every process's argv:
  a `ZDTD` carrying `.` or `+` matched more than the path, and one chosen to
  match broadly killed unrelated processes. The pattern is now literal-quoted
  by a shared `ere_quote_literal` helper in `proton_paths.sh`, used for both
  the layout sweep and the `ZDTD` one.
- The join harnesses' `log()` wrote its line unflattened, so an env override
  carrying a newline (`ZDTD_BIN` and the prefix-derived log paths, which
  reach the control log unvalidated) could forge a `result=` line that
  `zero_nre_join_loop.sh` reads as a cycle verdict. Flattening moved into the
  writer, so every current and future argument is covered; the call sites that
  already flattened are unaffected.
- `SCRATCH` was used for the age/count prune and the per-attempt `rm` with no
  path check, so a relative value or one holding `..` had the prune reach files
  outside the artifact dir. Both harnesses now require an absolute path with
  no `..` and fall back to the default with a warning, the treatment `CYCLE`
  already got.
- The temp files the launcher, `package.sh`, and `zero_nre_join_loop.sh` write
  through a redirect (`platform.cfg.tmp.$$`, the build record, the summary
  trim) were not unlinked first, so a pre-created symlink at that predictable
  pid-suffixed name was written through. They are removed before the write.
- `ConnectTarget` echoed launch-context values through the character-only
  sanitizer, so a `-connect=` value of any length chosen by a clicked
  `steam://run` URL landed whole in the client log the join harnesses scan,
  and a connect failure appended the exception's `StackTrace` unflattened to a
  line that also reaches the on-screen F1 console. The echoes now use the
  length-capping `EchoForMessage`, and the stack trace is flattened with the
  rest of the message.
- `WindowTrace.Emit` and the open-window heartbeat wrote window names straight
  to the log, the only string sinks in the mod that bypassed `LogText`.
- `Environment.GetCommandLineArgs()` was read unguarded next to a guarded env
  read, so a host that blocks it turned a launch-context scan into an escaping
  exception out of a static initializer: the mod stopped loading at all, with
  no trace of its own. It now reads as "no argv target", the same as the
  unreadable env var beside it.
- `env_bool` echoed an undocumented boolean value unsanitized, so a newline or
  a bidi override in `CLIENT_MUTE` / `START_SERVER` forged a second line into
  the launcher's stderr, which the join harnesses archive. The shared reader
  now flattens the value like every other rejection line, and sources
  `log_sanitize.sh` itself so a script that reads a knob cannot miss it.
- A whitespace-only `GFX_API` trimmed to empty and fell into the invalid-value
  arm, aborting the launch, where "unset and empty mean the default" says it
  takes `d3d11`. It is now normalized before the default is chosen.
- The console echo (`LogText.EchoForMessage`) cut a pasted value at 40 UTF-16
  code units, so a long paste of emoji or CJK could end in a lone surrogate
  and print as U+FFFD. It now counts code points, like `ConnectTarget`'s
  stricter twin, so the module's claim that every cap is code-point based
  holds.
- `PlayerNames.CapToMaxLength` capped at 24 UTF-16 units and had no caller in
  the mod; the code-point cap next to it is the one in use. The dead method is
  gone and its harness assertions target the real cap.
- The EULA row in the README named a `HasAcceptedLatestEula=true` write that
  the mod does not do: the prefs written are `EulaLatestVersion` and
  `EulaVersionAccepted`, and only the getter is patched.

### Added

- The game build the mod targets is recorded once in `pyproject.toml`
  (`[tool.fastconnect] game-version`) and `test_version_sync.sh` fails when
  `ConnectTarget.PlaceholderServerVersion` moves without it. That constant is
  what the client advertises when its own version string cannot be read, and
  nothing in the tree recorded it, so a game update left the previous build
  on the wire.
- `scripts/test_log_writer_sanitize.sh` pins the two rules the fixes above
  rest on: a harness `log()` flattens an embedded newline so it cannot forge a
  `result=` marker, and `ere_quote_literal` escapes the ERE metacharacters a
  `pkill -f` pattern would otherwise read.
- `docs/PRIVACY.md` maps the personal data the mod touches: the player display
  name (source, the pref it is stored in, the server it reaches, and the fact
  that it never reaches the client log), the synthetic platform id the
  Steam-less path sends, and the harness artifacts with their pruning. It also
  states the erasure path, since a stored name is stock preference state a
  user may want cleared. `test_player_name_override.sh` pins the claims that
  name this code, so the page cannot drift from the flow it describes.
- `ruff` selects the correctness groups the tree already passes: `BLE`,
  `TRY`, `C90`, `N`, `PIE`, `FLY`, `G`, `LOG`, `SLF`, `ASYNC`, `FA`, `TD`,
  `FIX` and `ANN`. A blind except or a swallowed error had no enabled rule
  naming it. `COM` stays at `COM818`/`COM819` because `COM812` contradicts
  `ruff format`, and the two would rewrite the same line.
- The release zip and `make install` now ship `LICENSE` alongside the
  assembly and the manifest. The mod is redistributed as a zip, so its
  terms have to travel with the payload.
- `dotnet-coverage`, the build dependency behind `make coverage`, is declared
  in `.config/dotnet-tools.json` and installed with `dotnet tool restore`, the
  way the SDK band lives in `global.json` and the Python gates live in
  `pyproject.toml`. Its version was a `--version` argument in one workflow
  file, and a local run was advised to `dotnet tool install -g
  dotnet-coverage`, which resolves whatever the index serves that day.
  `test_tool_manifest.sh` pins the invariant.

### Fixed

- A digit string too long for bash arithmetic no longer validates as a
  numeric knob. `$(( ))` is signed 64-bit and wraps, so a value of
  `18446744073709551633` (2^64 + 17) reached `PORT` as port 17, and a
  `TIMEOUT_SEC` or `CLIENT_MUTE_TIMEOUT` of the same shape became a
  deadline already in the past, ending the join or mute poll on its first
  check. `is_uint` compares the text against the intmax ceiling before
  any of it reaches `$(( ))`, and `is_tcp_port` and the mute window are
  built on it. `TIMEOUT_SEC`, `SETTLE_SEC`, and `MAX_ATTEMPTS` read
  through `is_uint` instead of a bare digit regex.

- `CLIENT_MUTE_TIMEOUT` is capped at 3600 seconds, well above any real
  poll window. The launcher and `mute_client_audio.sh` share one
  `is_mute_wait` check, so a window the launcher announces is a window the
  helper runs.
- A stored `PlayerName` that cannot be read is no longer overwritten. The
  automation fallback swallowed the read failure and treated it as an empty
  stored name, so a transient `GamePrefs` failure replaced the player's
  identity with a generated one and saved it. The read failure is logged and
  the pref is left alone.
- A failed prefs write is named for what failed: the Discord and EULA accepts
  shared one catch whose message blamed Discord for an EULA failure, which is
  the one that blocks startup. They are separate catches now, and every
  message in that file carries the exception type as the rest already did.
- The three `World.LoadWorld` / `createWorld` / `StartAsServer` postfixes no
  longer let their own failure escape into stock game code. The wrap ran the
  local-host probe (and, in the world-load drain, the reflection prologue)
  synchronously inside the postfix, so a throw there stopped the world from
  loading and the failure was the game's to report. A failed wrap now logs and
  leaves the stock enumerator in place.
- The frame-hitch monitor reports a failure instead of going quiet. Its first
  throw ended the coroutine, and the start latch meant nothing restarted it,
  so `diag on` stopped reporting hitches for the rest of the session.
- The console reply is written even when the game logger throws, and no
  longer throws into the stock console dispatcher when it does.
- A failed `Harmony` patch now logs the applied/skipped summary at error
  severity. A game update that renames one target left the mod half-patched,
  with the per-patch warnings easy to miss in a log the harnesses grep.
- `package.sh` fails when the archive cannot be hashed instead of writing a
  build record with an empty `sha256`, checks for `sha256sum` and `git` up
  front (both are on the packaging path, and `check_prereqs.sh` now requires
  them), writes the record through a temp file and a rename, and exits on
  INT/TERM instead of running on into a "stage dir does not exist" error
  after the trap already removed the stage.
- The staged mod tree is never partial at its real path. `stage_mod.sh`
  filled it in place, so a copy that failed part-way left a mod folder with
  one or two of its three payload files for a later `repro_zip.sh` to archive
  as if it were complete; the tree is filled under a temp name and moved into
  place. `repro_zip.sh` likewise zips into a temp file and renames, so a
  failed or interrupted `zip` leaves no truncated archive at the release path.
- `zero_nre_join_loop.sh` no longer scores a stale log as this run's evidence.
  The per-attempt and confirmation copies live for three days and the
  one-shot's own copy is allowed to fail, so a lost artifact satisfied the
  existence test and produced `PASS` from a previous run's log. Both are
  cleared with the client log before each cycle, a missing confirmation log is
  named, and a one-shot exit without a `result=` line is recorded as one.
- `zero_nre_join_loop.sh` requires its own server to be the listener. The
  readiness probe matched any listener on the port, so a port left held by an
  earlier server let the loop spend all its attempts against a server that had
  died on the bind failure.
- The listen probe in `zero_nre_join_loop.sh` and `one_shot_join.sh` no
  longer reports a live port as absent. `ss | grep -q` closes the pipe on the
  first match, and under `pipefail` a still-writing `ss` takes SIGPIPE, which
  becomes the pipeline status; the output is read into a variable first.
- A `grep` error in `log_markers.sh` is no longer memoized as a miss. Status 2
  (unreadable log, bad pattern) advanced the resume offset past bytes that
  were never scanned, so the pattern answered "not seen" for the rest of the
  cycle. The error is reported once and the offset is left alone.
- A control log written from a client log that was never captured now says so.
  `write_join_evidence` discarded `grep`'s error and wrote an empty evidence
  section that read exactly like a log with no matching lines.
- `restart_pair.sh` stops its server with TERM, then KILL, then a reap on the
  not-ready path (it was TERM only, so a server that ignored it held the port
  after the script reported failure), and sweeps the previous server by the
  resolved `ZDTD` path rather than only by the default `zig-out/bin/zdtd`
  layout, which a `ZDTD=` override made a no-op.
- `changelog_gate.sh` checks the tagged section's entries and impact group
  when that section is last in the file. The check only ran when a *later*
  heading was reached, so a single-section changelog was never checked for
  content at all, and the newest-section error now distinguishes a changelog
  that runs ahead of the tag from one with no notes for it.
- A second connect request while the first is still dialling is refused
  instead of dialling again. `ConnectionManager.Connect` hands the target to
  LiteNetLib and returns, and `IsConnected` reports the outcome only after
  the handshake, so the auto-join coroutine landing on the same menu the
  F1 `connect` command was typed into (or that command typed twice) started a
  second attempt at a server the first one was still dialling. The latch is
  released for a real retry: when the join gate observes the live connection,
  when the client leaves that session, and when an attempt that never
  reported back outlives a 30 s window, so a failed join can be re-run. Gate:
  the `connectrequest` mode of `scripts/test_connect_target_parse.sh`.
- `make install` and `make uninstall` refuse an empty or root-level
  `MODS_DIR`. Either value collapses `INSTALL_DIR` to a top-level path
  that `uninstall` deletes recursively; the guard fails before anything
  touches disk.
- `one_shot_join.sh` stops the detached launcher as a process group. It runs
  under `setsid`, and the cleanup trap signalled only the launcher's own pid,
  so the mute poller and the Proton stack it forked survived the cycle, and
  the launcher's EXIT trap (the one that restores `platform.cfg`) never ran
  for a launcher that took the KILL. The stop goes through a helper that
  signals the group when the pid leads one and falls back to the plain pid
  otherwise, re-checks liveness before the KILL so a recycled pid is never
  signalled, and reaps the child. Gate:
  `scripts/test_one_shot_launcher_group.sh`.
- The `CLIENT_PLATFORM=local` swap no longer truncates `platform.cfg` in
  place. The bare redirect emptied the file before a byte of the Local
  config was written, so a crash, a signal, or a full disk left the client
  reading an empty platform selection. The Local config is now written to a
  temp file in the same directory and renamed, the same rule the backup
  already used; a swap that cannot be written leaves the Steam config in
  place and says so.
- `7DTD_PLAYER_NAME` is honoured in every mode, not only in automation boot
  mode. It was applied inside the `AutomationMode.Enabled` block, so a client
  launched without a join target (the Local-platform launch the variable
  exists for) silently kept the stored `PlayerName` pref. The no-env
  fallback that stores a generated name stays automation-only, so an
  ordinary client launch still leaves the stored identity alone.
- `LogText.SanitizeForLog` flattens U+2028 and U+2029, so the leaf every
  echoed env value and F1 console echo passes through no longer lets a
  Unicode line separator split a log line. It is now the same character set
  as `ConnectTarget.SanitizeForLog` and the shell twin
  `sanitize_log_text`, which is what R3 in `docs/THREAT_MODEL.md` claims.
- `PlayerNames.Resolve()` sanitizes the OS account / machine name it stores,
  the same rule the `7DTD_PLAYER_NAME` path already used. An account name
  carrying a control or bidi character reached the server and its logs
  unflattened.
- A rejected `7DTD_CONNECT` no longer reads as fatal when a later launch
  source resolves. The warning ends with "auto-join disabled", which is true
  of the rejected value alone, but the argv scan continues and a valid
  `-connect=` then joined; one follow-up line names the source that won.
- The F1 `connect` command names extra tokens it cannot use instead of
  dropping them. `connect 127.0.0.1 27025 9999` joined 27025 silently, the
  same shape as the dropped-port-argument warning `MergePortArg` already
  emits.
- A failed EULA-accept is no longer latched as a resolved gate.
  `EulaSkip.BlockGateWindow` set its once-per-process latch after every
  attempt, so a `GamePrefs` write that threw left the latch armed with the
  profile unwritten: the gate window stayed blocked, no later request retried
  the write, and the next launch opened the EULA window again. The latch is
  now armed by the accept; the `MainMenuOpened` dispatch still runs on the
  failure path so a client is never left on a gate window.
- Auto-join no longer spends its one attempt before making it. `ModApi`
  armed `_autoTried` on entry to the `MainMenuOpened` handler, so a launch
  context that threw (blocked env read or logger) left the latch armed with
  no join attempted, and every later main-menu open returned at the latch: the
  client never auto-joined for the rest of the session. The latch is armed
  after the target resolves, and a throwing resolution announces once through
  `ProbeFailure` and retries on the next menu open. Pinned by
  `scripts/test_auto_join_latch.sh`.
- Client-log evidence copied into the join control log is sanitized and
  prefixed. `one_shot_join.sh` appended the client log's join lines to
  `client-lifecycle-<cycle>.txt` verbatim, and the client log carries
  server-supplied text (world strings, chat), so a server could put a
  `result=joined` or `=== one_shot_join ...` line in the control log that
  join tooling greps. The copy now goes through `sanitize_log_text` (so a
  U+2028 or C1 NEL in a server line cannot split it) behind a `log:`
  prefix, and it no longer reaches stdout, which `zero_nre_join_loop.sh`
  captures. The raw client-log copy is unchanged. See
  `scripts/join_evidence.sh` and the new `scripts/test_join_evidence.sh`
  gate.
- `package.sh` refuses a version that is not a single filename-safe
  component. A tag such as `release/1.0` describes with a slash in it, and
  `VERSION=` is a documented override, so either could write the zip and
  its `.buildinfo` outside `dist/`. The value is now rejected with exit 2
  (usage error), the same rule `one_shot_join.sh` applies to `CYCLE`.

- The launcher no longer blocks forever on its mute poller at exit. A bare
  `wait` has no timeout, so a helper that did not answer TERM (a wedged
  audio server leaves the pactl call blocked) held the shell that started
  the client, and in `CLIENT_PLATFORM=local` left `platform.cfg` swapped to
  Local for as long as it hung. The reap is now bounded by
  `MUTE_POLL_STOP_GRACE_SEC` (default 5), the helper leads its own process
  group so the wedged pactl call dies with it, and the shutdown path names
  the kill.
- `unmute_client_audio.sh` exits 1 when `pactl` refuses a live stream
  instead of reporting it unmuted, and `apply_game_stream_mute` returns the
  failure. The mute helper still ignores it: audio never fails a launch.
- Every `pactl` call in the audio helpers is bounded by
  `AUDIO_PACTL_TIMEOUT_SEC` (10s, where coreutils `timeout` exists), so a
  wedged Pulse/PipeWire server cannot stall the poll past its window.
- The Local-platform `platform.cfg` backup is written to a staging file and
  renamed into the slot. A `cp` cut short by a full disk, a signal, or a
  crash left a truncated backup, and the next launch's self-heal moved that
  half file over the real config.
- `LogText.SanitizeForLog` flattens the invisible-format characters
  (bidi overrides, zero-width marks, BOM) as `ConnectTarget.SanitizeForLog`
  did. The `-connect=` and `7DTD_CONNECT` warning paths sanitize through
  LogText, so those echoes reached the client log with a bidi override
  intact, unlike the shell twin in `scripts/log_sanitize.sh`.
- A truncated client log no longer keeps a marker cached as seen. `log_seen`
  recovered its scan offset when the log shrank below it but left the memoized
  match, so a poll after a truncate could report a join from the previous log's
  bytes. A log shorter than the longest one seen now drops every memoized
  verdict, and `one_shot_join.sh` invalidates the memo at the truncation it
  performs. A file replaced by one of exactly the same size is still
  indistinguishable from an un-grown log.
- A whitespace-only `CLIENT_MUTE` (or `SEVEN_DAYS_TO_DIE_CLIENT_MUTE`) reads as
  the documented default, mute on, instead of as an opt-out. The launcher
  compared the trimmed value, where a blank value and an opt-out both resolve
  to empty, so `CLIENT_MUTE=" "` muted nothing.
- The built mod no longer embeds the absolute path of its own pdb, so the same
  source compiles to the same dll bytes in any checkout directory. Debug
  symbols were never shipped, so they are off.
- `make package` writes a `<version>.buildinfo` beside the archive with the
  commit, dirty state, `SOURCE_DATE_EPOCH`, dotnet SDK version, and the
  archive's sha256, so a rebuild has a recorded environment to start from.
- The net48 reference assemblies are an explicit `PackageReference` locked by
  `Source/ConnectMod/packages.lock.json`, and `make build` restores in locked
  mode. The SDK had been pulling that package in implicitly at whatever
  version it defaulted to.
- The player-name cap counts code points instead of UTF-16 code units and cuts
  only on code-point boundaries, so an emoji or CJK name is no longer charged
  two characters for one glyph and a cut can no longer leave a lone surrogate
  that the prefs store and the wire encoder turn into U+FFFD. The cap also
  normalizes to NFC, so the NFD spelling of a name (what a macOS account hands
  back) and the NFC spelling are one identity to the server that rejects
  duplicate names. The F1 `connect` error echo shares the same rule.
- `SanitizeForLog` and its shell twin `sanitize_log_text` flatten U+2028 and
  U+2029, and the shell twin now covers the C1 block as well. A C1 NEL or a
  Unicode line separator in `7DTD_CONNECT` or `CYCLE` laid the log line out in
  two in a log reader even though grep does not break on it; the C# side
  already flattened the C1 block, so the two sides also disagreed.
- `stage_mod.sh` empties the staged mod folder before copying, so staging
  into a stage root a previous run used yields the payload alone. The zip was
  otherwise the union of the payload and whatever the stage root already held,
  and the artifact depended on the stage root's history rather than on the
  build. `test_stage_mod.sh` pins it.
- The EULA gate is handled once per process. `windowEula` is requested by
  name, and every request accepted the EULA (a `GamePrefs` save) and re-fired
  `ModEvents.MainMenuOpened` at every mod; a second request in a session
  repeated both for a gate already resolved. A repeat still blocks the window
  and does no work; a first attempt that threw leaves the latch unset, so the
  next request retries it. `test_eula_gate_once.sh` pins the ordering.
- `coverage-cs.sh` removes the previous merged report before merging, so a
  second coverage run in one tree does not depend on the merger overwriting a
  file it already wrote, and a failed merge leaves no stale report for the
  badge to render.
- A failure inside `ConnectReady.IsReady`'s cross-user or native-user probe,
  and one inside the per-frame frame uncap, are announced once and then muted
  instead of once per call. Both sit in loops (the 10 Hz join gate, and
  `UpdateFPSCap`), so a throwing platform read wrote ten log lines a second
  into the log the join harnesses grep.
- `zero_nre_join_loop.sh` stops the server it started by pid and reaps it,
  instead of only sweeping processes named `zdtd`. A server launched through
  the `ZDTD_BIN` override whose binary is not named `zdtd` was invisible to
  the sweep, so the run left it ticking its world and holding the port, and
  the next run waited on that stale listener.
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
- `launch_client.sh` mutes the client again for a blank `CLIENT_MUTE`. A
  whitespace-only value trimmed to empty, which the opt-out `case` then read as
  "leave audio on", the opposite of the documented contract and of what the
  mod's `EnvFlags` twin does. `test_blank_client_mute_keeps_the_mute_on_default`
  pins it.
- The launcher tests that stub `pactl` keep the host `PATH` behind the stub
  directory instead of replacing it, so a machine whose `jq` lives outside the
  stub directory no longer fails the mute assertions with the helper's
  "jq required" warning.
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
- Every deadline in the mod is measured on `Time.realtimeSinceStartup` instead
  of `Time.unscaledTime`. `unscaledTime` accumulates `unscaledDeltaTime`, which
  Unity clamps to `Time.maximumDeltaTime` (0.333 s), so on the stalling boot
  these gates exist to bound, a stalled frame advanced the clock by a third of
  a second: the 45 s connect wait, the 30 s in-flight connect-request window,
  and the native-user and cross-user (EOS) settle windows all ran for minutes
  of wall clock before expiring. The `t=` seconds on the connect-wait and
  heartbeat lines now come off the same clock, so a reported wait matches the
  budget it is checked against. The `connectrequest` mode of
  `scripts/test_connect_target_parse.sh` greps every mod source for a
  reintroduced `Time.unscaledTime`.

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

[Unreleased]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.13.0...HEAD
[0.13.0]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.12.0...v0.13.0
[0.12.0]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.11.0...v0.12.0
[0.11.0]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.10.5...v0.11.0
[0.10.5]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.10.4...v0.10.5
[0.10.4]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.10.3...v0.10.4
[0.10.3]: https://github.com/hordeforge/7dtd-fastconnect/compare/v0.10.2...v0.10.3
[0.10.2]: https://github.com/hordeforge/7dtd-fastconnect/releases/tag/v0.10.2
