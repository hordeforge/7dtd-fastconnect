# Threat Model: 7dtd-fastconnect

Client-only mod for 7 Days to Die: joins servers by IP without Steam
`steam://connect`, plus launch automation plumbing. This document is the
systemic view of what this repository's code can be attacked through, what it
puts at risk, and which mitigations exist in the code versus which are
missing. Individual vulnerabilities are not fixed here; each gap below names a
location and is handed to sec-review.

Scope: this repository only. The stock game client, the dedicated server
(zdtd / stock dedi), Steam/EOS netcode, and EAC are outside it. The mod adds
no S2C handling that synthesizes client state (AGENTS.md rules 4-5); the
exception is a class of read-only, `diag`-gated observation hooks described
under boundary 7 (`NetPackageEntityAliveFlags.ProcessPackage` at
Source/ConnectMod/AliveFlagsTrace.cs:14-25, and `EntityAlive.set_Spawned` /
`OnAddedToWorld` at Source/ConnectMod/SpawnedTrace.cs:7-8,31), which log
fields the stock handler already parsed and change nothing.

- Last reviewed: 2026-09-28 (against the v0.12.0 tree)
- Owner and review cadence are organizational decisions; none is assigned in
  this document.

## Risk-ranked summary

| # | Risk | Boundary | Status |
|---|---|---|---|
| R1 | Auth downgrade under automation: empty auth ticket + synthetic platform identity let a Steam-less client join EAC-off LAN servers with a predictable identity | mod → server auth | Intentional by design; gated but worth an explicit deployment decision |
| R2 | Attacker-shapable `-connect=` text (e.g. a clicked `steam://run` URL) aims the client's outbound join at an attacker-chosen host | desktop → launch context → outbound network | Parse validation only; accepted residual by design |
| R3 | Log/marker forging via control characters and Unicode line separators in echoed env/argv values | launch context → client log → harness greps | Mitigated on both sides; one C# implementation plus a shell twin, which still differ in how they treat invisible-format characters (the residual risk) |
| R4 | Release zips are built on a maintainer machine and attached manually; the shipped artifact's digest is self-asserted over an unsigned attachment, with no independent channel to check it against | build → runtime | Controls exist and are recorded (reproducible bytes, sha256 build record, archive-vs-stage comparison, tag/version gate); the residual is that every one of them is produced by the same machine that produced the payload |
| R5 | `pkill -9 -f '7DaysToDie'` / `pgrep -f` patterns match unrelated processes whose command line contains the substring | local operator tooling | Partial: the children this run started are stopped by owned pid and process group (scripts/one_shot_join.sh:185-197), so only the `pgrep -f`/`pkill -f` sweeps stay pattern-based; local DoS blast radius |
| R6 | Verbose diagnostics are unbounded: `diag on` makes the window trace emit per open/close, the spawn/flags traces emit per spawn event and per alive-flags package (with a full managed stack trace each), and the hitch monitor emit per slow frame for the process lifetime, into the same log the join harnesses grep | local user → client log → harness/disk | Gated behind a local opt-in and a frame threshold; no volume cap |
| R7 | Two paths write process-global Unity settings and never restore them: the automation boot unblock (`runInBackground`, `vSyncCount`, `targetFrameRate`, `backgroundLoadingPriority`) and the diagnostic spawn heartbeat's `runInBackground` write, which is reachable from `diag on` with no automation gate | local user → engine process state | Gated by mode for the first, ungated for the second; the restore in `finally` covers only the local-host load wrapper |

Risks are renumbered from the previous revision. The former R1, an
env-controlled `File.WriteAllText` destination via `7DTD_DUMP_BLOCK_IDS_PATH`,
is closed in v0.11.0: the block-id dumper that owned that write path was
removed, and the mod now does no direct file I/O at all. The writes that
remain go through the game's own `GamePrefs.Save`
(Source/ConnectMod/EulaSkip.cs:28, ModApi.cs:32,197), which is the consent
state named under Assets, not an arbitrary path. R6 and R7 were added in
v0.12.0, when the diagnostic traces and the local-host hitch monitor grew
past the boot heartbeat that was there before; R6 grew again when the
spawn/flags traces joined the window trace behind the same gate, and R7
names the engine settings those paths write.

The single highest-value correction for readers: nothing in this repo stores
secrets or listens on the network. The assets are the local machine, the
player identity the client presents to servers, and the trust harnesses place
in client-log markers.

## Assets

- **Local machine integrity**: game install and user config mutated by launch
  scripts (`platform.cfg` swap, scripts/launch_client.sh:180-246) and by the
  mod's own prefs writes (player name, EULA acceptance, Discord and intro
  prefs, Source/ConnectMod/ModApi.cs:54-62,126-172). The mod writes no
  arbitrary file: the block-id dumper that did was removed in v0.11.0.
- **Player identity**: the platform id and display name the client presents to
  servers. Under automation this can be synthetic and predictable
  (Source/ConnectMod/AuthFallbackPatches.cs:49-95,
  Source/ConnectMod/PlayerNames.cs:31-49) or env-selected
  (Source/ConnectMod/ModApi.cs:126-172).
- **Join-decision trust**: harnesses decide pass/fail by grepping client-log
  markers (scripts/one_shot_join.sh:135,325-365; scripts/log_markers.sh:53,60-104).
  The log is therefore an asset: forged markers forge results, and a log the
  mod fills with diagnostics is a log the harness must still scan.
- **Consent state**: automation force-accepts the EULA and skips news/Discord
  gates (Source/ConnectMod/ModApi.cs:49-60, Source/ConnectMod/EulaSkip.cs:9-22).
- **Session stability**: a Local host that hangs at "Initializing world" is
  the failure the local-host load patches exist to remove, so holding sync
  loading and the raised load priority must be released when startup ends
  (Source/ConnectMod/LocalHostWorldLoadPatches.cs:72-84,123-133).
- **Availability**: unattended launches must terminate; several bounded waits
  exist precisely so a wedged component cannot hang the machine's tooling
  (DNS 5 s, connect gate 45 s, prefab prewarm 60 s, async drain 60 s).
- **Secrets**: none are stored, generated, or rotated by this repository.
  Steam/EOS credentials live entirely in the stock client; this mod only
  substitutes an *empty* ticket when no login exists
  (Source/ConnectMod/AuthFallbackPatches.cs:9-43,159-187).

## Entry points

Every entry point reads data from outside the process boundary:

| Entry point | Where parsed | Notes |
|---|---|---|
| Env `7DTD_CONNECT` | Source/ConnectMod/ConnectTarget.cs:228-236; scripts read it too (scripts/launch_client.sh:81,389,411; scripts/one_shot_join.sh:87,258,316; scripts/restart_pair.sh:142) | Attacker-shapable: a clicked `steam://run` URL chooses `-connect=` text (in-code rationale at ConnectTarget.cs:39-61 and LogText.cs:42-63) |
| argv `-connect=` / `+connect=` / `+connect_lobby` | ConnectTarget.cs:238-274 | Same threat model as the env var |
| F1 console `connect` / `7dtdconnect` / `joinip` | Source/ConnectMod/ConsoleCmdConnect.cs:9,28-68 | Local keyboard input; allowed in main menu (:24) |
| F1 console `diag` / `7dtd_diag` / `zdiag` | Source/ConnectMod/ConsoleCmdDiag.cs:9,93-128 | Flips verbose traces at runtime; the console override outranks the env snapshot (Source/ConnectMod/DiagToggle.cs:19-47). Its output is a single write point shared with the connect command (Source/ConnectMod/ConsoleOutput.cs:12-45) |
| Console command replies (all F1 commands) | Source/ConnectMod/ConsoleOutput.cs:29-45 | The one place a console line reaches the log. It writes the caller's string verbatim, so flattening stays a call-site responsibility: `LogText.EchoForMessage` at ConsoleCmdConnect.cs:48,56 and ConsoleCmdDiag.cs:124. The log line is what the harnesses grep, not the on-screen echo |
| Env `7DTD_PLAYER_NAME` | ModApi.cs:118-135 | Sets `GamePrefs.PlayerName`, persisted to the client profile |
| Env `7DTD_CONNECT_AUTOMATION`, `7DTD_CONNECT_DEBUG`, `7DTD_CONNECT_FORCE_LOAD_SYNC` | Source/ConnectMod/AutomationMode.cs:12-24, Source/ConnectMod/DiagToggle.cs:5, Source/ConnectMod/BootUnblock.cs | Boolean flags; truthiness shared in Source/ConnectMod/EnvFlags.cs:12-38 |
| Script env: `GAME`, `PROTON`, `COMPAT`, `STEAM_ROOT`, `GFX_API`, `CLIENT_MUTE_TIMEOUT`, `CLIENT_PLATFORM`, `PORT`, `HOST`, `TIMEOUT_SEC`, `SETTLE_SEC`, `CYCLE`, `SCRATCH`, `ZDTD_BIN`, `START_SERVER`, `WINEDLLOVERRIDES` | scripts/launch_client.sh:48-147,369; scripts/one_shot_join.sh:76-117; scripts/restart_pair.sh:36-52; scripts/mute_client_audio.sh:40-52 | Several are validated before use (see Mitigations); `GAME`/`PROTON`/`COMPAT` select executables by design. `WINEDLLOVERRIDES` is appended to, not overwritten (launch_client.sh:369), so anything already in the operator's env also reaches the Wine process |
| Script env: `MAX_ATTEMPTS`, `MAP_DIR`, `GAME_DIR`, `WORLD_DIR`, `STEAM_APPID`, `CLIENT_LOG_SRC` | scripts/zero_nre_join_loop.sh:57-85 | The stock-join regression loop. `MAX_ATTEMPTS`/`TIMEOUT_SEC` are re-validated with the shared numeric guard before any attempt (zero_nre_join_loop.sh:225-236); `MAP_DIR`/`GAME_DIR`/`WORLD_DIR` name server-side paths and are outside this repository's ownership; `CLIENT_LOG_SRC` overrides the client log the loop scores, so it decides which evidence file is trusted |
| Build env: `VERSION`, `SOURCE_DATE_EPOCH` | scripts/package.sh:77-103; scripts/repro_zip.sh:44-59 | Shape the shipped artifact's name and every timestamp in it. Both are validated: `VERSION` must be one filename-safe component (package.sh:95-98), `SOURCE_DATE_EPOCH` a non-negative integer (repro_zip.sh:44-54). A tree with uncommitted tracked changes is named `...-dirty` rather than under the tag (package.sh:86-88) |
| Hardcoded process args added by the launcher | scripts/launch_client.sh:83 (`-skipintro -SkipNewsScreen=true -disablenativeinput`) | Not operator input, but they change what the client loads; `-disablenativeinput` was added for Proton boot stability, not for a security control |
| Client log file written by the game process | parsed by scripts/log_markers.sh:60-104 from one_shot_join.sh:325-365, copied at one_shot_join.sh:380, rescanned by zero_nre_join_loop.sh:210,216-221 | Process-to-harness boundary; content is semi-trusted |
| Server-influenced client-log lines copied into the harness control log | scripts/join_evidence.sh:29-39 (`write_join_evidence`), called from one_shot_join.sh:391 | The server chooses the text: names, world strings and errors reach the client log verbatim, and the control log is the file the harnesses grep for markers |
| Outbound network connect | ConnectTarget.cs:388-482 (`ConnectionManager.Connect` at :464) after DNS resolution :299-354 | The only network traffic this repo initiates |
| Engine load pipeline (Local host) | Source/ConnectMod/LocalHostWorldLoadPatches.cs:440-456 patches `World.LoadWorld`, `GameManager.createWorld`, `GameManager.StartAsServer` | No external input; the wrapping coroutines read engine internals by reflection :391-418 and change global load settings :72-84,370-389 |

Deployment surface: GitHub Actions workflows run `make test` / coverage /
tag-gating with least privilege and SHA-pinned actions
(.github/workflows/ci.yml:15-23,30-41, .github/workflows/release.yml:18-29).
The release zip itself is built locally and attached manually
(.github/workflows/release.yml:8-12), so the pipeline never proves what ships.
The packaging path that does run on a maintainer machine is the one the
mitigations table covers: `make package` (scripts/package.sh) builds, zips
reproducibly, reads the archive back against the staged tree, and writes a
build record beside it. Its own inputs are the build machine and git
metadata, which is why R4 is a provenance gap rather than an unmitigated
one.

## Trust boundaries and data flow

1. **Desktop integration → launch context** (env vars, argv): anything that
   can plant `7DTD_CONNECT` or a `-connect=` argument chooses where this
   client connects and what identity name it presents. Crossing point: the
   parsers above; sanitization at the log echo points only.
2. **Launch context → outbound network**: `TryParse` output feeds
   `ConnectionManager.Connect` directly (ConnectTarget.cs:222-277). There is
   no allow-list; any resolvable host:port is joined. Server responses are
   applied entirely by stock engine code; the mod's only contact with them
   is the read-only, `diag`-gated observation hooks under boundary 7, which
   consume parsed fields and synthesize nothing (AGENTS.md rule 5).
3. **Game process → shell harnesses**: lifecycle scripts treat the client log
   as evidence. Marker regexes decide `result=joined`
   (one_shot_join.sh:325-365). Anything able to write those bytes decides the
   verdict; the scripts assume only the game writes them.
4. **Scripts → on-disk config**: `CLIENT_PLATFORM=local` replaces
   `$GAME/platform.cfg` after backing it up, restores on exit, self-heals a
   previous interrupted swap, refuses when the backup cannot restore, and holds
   an exclusive lock (`platform.cfg.re-local.lock`) so a second launcher on the
   same install reuses that swap instead of taking the single backup slot
   (scripts/launch_client.sh:180-246). The replacement is written to a temp
   file in the same directory and renamed over the live config
   (launch_client.sh:231-243), so a crash, a signal, or a full disk mid-write
   leaves the Steam config in place rather than an empty file; a swap that
   cannot be written is abandoned with the original intact. Failure here
   silently changes which platform identity the user's next manual launch uses.
5. **Automation mode → engine internals**: Harmony patches tagged
   `[AutomationPatch]` replace auth-ticket production and platform identity
   (AuthFallbackPatches.cs) and are applied only when automation boot mode is
   on (ModApi.cs:62-75 skips them otherwise; gate detection at
   AutomationMode.cs:17-24 auto-enables whenever a launch target exists).
6. **Local host session → engine load pipeline** (new in v0.12.0): the
   local-host world-load patches are *not* automation-gated; they activate on
   any non-automation Local server session
   (LocalHostWorldLoadPatches.cs:51-53). The mod therefore rewrites engine
   coroutines, raises `Application.backgroundLoadingPriority` and
   `runInBackground`, and holds sync addressable loading through a private
   static field (LocalHostWorldLoadPatches.cs:306-334) in ordinary host
   play. Restores are in a `finally` (:123-133); a hard process kill mid-load
   leaves nothing to restore because the state dies with the process.
7. **Local user → client log** (grown in v0.12.0): `diag on` or
   `7DTD_CONNECT_DEBUG` turns on per-window traces
   (Source/ConnectMod/WindowTrace.cs:20-30), the in-game spawn heartbeat
   (Source/ConnectMod/SpawnStateHeartbeat.cs:19-21), the load heartbeat
   (Source/ConnectMod/LoadStateProbe.cs:14-20) and the Local-host hitch
   monitor, which runs for the whole process lifetime and logs every frame
   over 0.2 s (PerfTrace.cs:36-40,57-92). The two heartbeats share one 5 s cadence
   and the same load-gate math (Source/ConnectMod/LoadGate.cs:10-40). The
   same gate opens two per-event traces with no cadence at all:
   `Environment.StackTrace` on every
   `EntityAlive.set_Spawned` and `OnAddedToWorld` for the local player
   (Source/ConnectMod/SpawnedTrace.cs:19,43) and a line per
   `NetPackageEntityAliveFlags` package carrying the primary player's id
   (Source/ConnectMod/AliveFlagsTrace.cs:14-25), which runs inside a stock
   packet handler. `ProbeFailure` (Source/ConnectMod/ProbeFailure.cs:15-43)
   is the shared announce-once channel all of them report a dead probe to.
8. **Local user → engine process state** (R7): the diagnostic spawn
   heartbeat writes `Application.runInBackground = true` from its first
   heartbeat and never puts it back
   (Source/ConnectMod/SpawnStateHeartbeat.cs:27). The only place in the mod
   that restores that setting is the local-host load wrapper's `finally`
   (Source/ConnectMod/LocalHostWorldLoadPatches.cs:151-152), which this
   write is not inside, and the automation boot unblock writes the same
   four Unity settings with no restore at all
   (Source/ConnectMod/BootUnblock.cs:62-66). So a trace that exists to
   observe the join is also a path to a process-global setting, and it is
   reachable from `diag on` with no automation gate involved.

Privilege transitions: none. Everything runs as the desktop user; no service,
no setuid, no elevated installer (`make install` copies files into the game's
`Mods/` dir, Makefile:178-183).

## Threats per boundary

**Desktop → launch context (STRIDE)**

- *Spoofing/tampering*: crafted `-connect=` text redirects the join (R2);
  crafted control characters forge log lines and harness markers (R3).
- *Repudiation*: weak. Launch echoes do record source labels ("auto-join from
  7DTD_CONNECT=...", ModApi.cs:204; "Connect by IP ... (requested host=...)",
  ConnectTarget.cs:462), so the origin of a join is visible in the log.
- *Information disclosure*: the join handshake reveals player identity to
  whichever host the target names. Nothing else leaves the process: the mod
  opens no listener and does no direct file I/O, and the only local writes
  are the game's own prefs and the harness's own log copies.
- *DoS*: hostname resolution is bounded at 5 s (ConnectTarget.cs:306-316) and
  the auto-join ready-wait at 45 s monotonic (ModApi.cs:236-246); a wedged
  resolver cannot freeze the menu thread indefinitely.
- *Elevation of privilege*: none available; the mod runs entirely inside the
  game process with no additional authority.

**Launch context → outbound network**

- A hostile "server" receives the login packet containing the platform id and
  player name (synthetic ones included). Ticket material is either real
  (Steam/EOS logged in, patches pass through: AuthFallbackPatches.cs:11-43,
  161-175) or empty by construction. Impact of leaking a synthetic identity is
  low; impact of joining a hostile host is engine-level and out of scope here,
  but the redirect itself is this repo's decision (R2).

**Game process → shell harnesses**

- *Tampering*: forged success markers flip `result=joined` without a server
  (abuse case A2). Values the *mod* echoes are flattened first (R3
  mitigation); values other local processes write are trusted implicitly.
- *Tampering via the server*: a hostile server names itself in lines that
  reach the client log verbatim, and those lines are copied into the control
  log the harnesses grep. The copy is sanitized and written behind a `  log: `
  prefix (join_evidence.sh:29-39), so a copied line cannot read as a marker;
  what remains is that the server chooses which lines the operator sees, up to
  80 of them per cycle.
- *DoS*: a diagnostic flood (R6) grows the file the poller greps; the
  memoized matcher bounds the re-scan cost per pattern, but the first pass
  over a multi-megabyte log and the harness's overall time budget are both
  spent on mod output.

**Scripts → on-disk config / processes**

- *Tampering/availability*: the `platform.cfg` swap has backup, refuse-on-
  unrestorable, atomic-rename, and self-heal paths (launch_client.sh:180-246), and is exclusive
  across concurrent launchers via `flock`; residual risk is losing the user's
  platform selection if both copies die mid-run.
- *Process targeting*: kill sweeps match substrings of any user's command line
  (`pkill -9 -f '7DaysToDie'`, restart_pair.sh:114; `pgrep -f
  '[/]7DaysToDie.exe|wine64-preloader.*7DaysToDie'`, one_shot_join.sh:166),
  so an unrelated process whose argv mentions the string gets killed (R5).

**Local host session → engine load pipeline**

- *Tampering*: the wrapper decides which coroutine yields reach Unity, and a
  wrong frame count (nine, LocalHostWorldLoadPatches.cs:31) changes world
  creation semantics. The count is a fixed property of the game build, so a
  game update is the trigger, not an attacker.
- *Denial of service*: a wedged LoadManager is bounded (prefab prewarm 60 s,
  :148-177; async drain 60 s, :259-270) and the drain result is logged, so a
  silent hang is not possible; but the sync-loading hold is released only
  from `Flatten`'s `finally` (:123-133, :204-209). Losing that release leaves
  the client in forced sync loading for the rest of the session.
- *Information disclosure*: the raised load priority and `runInBackground`
  are process-local Unity settings, restored in the same `finally`; no data
  leaves the machine. The restore is scoped to this wrapper: the automation
  boot unblock and the diagnostic spawn heartbeat write the same settings
  with no restore at all (R7).

**Local user → client log**

- *DoS*: unbounded diagnostic output (R6). The window trace is documented as
  spamming every tick in normal play (WindowTrace.cs:7-11), the hitch
  monitor logs every frame over 0.2 s forever, and the spawn traces have no
  cadence at all: one managed stack trace per spawn event, one line per
  alive-flags package. All need the local user to opt in, so this is
  self-inflicted, but the log is shared with the harness that grades joins.
- *Information disclosure*: the spawn trace's `Environment.StackTrace` puts
  the mod's and stock game's internal call paths into the client log, where
  they are copied into cycle evidence by the harness
  (scripts/one_shot_join.sh:380) and kept in the scratch dir. It is local
  diagnostic material with no secret in it, but it is the widest diagnostic
  the mod emits and it is emitted per event.
- *Spoofing*: diagnostic lines share the client log with harness markers; the
  mod's own echoes are sanitized (R3), and its diagnostic lines carry a fixed
  `[7dtd-fastconnect] ` prefix, so they do not contain marker text.

**Local user → engine process state (R7)**

- *Tampering*: `diag on` leaves the client rendering and ticking while
  unfocused for the rest of the process (SpawnStateHeartbeat.cs:27), and an
  automation boot leaves vSync off and the frame cap removed
  (BootUnblock.cs:62-66). A harness that reads a screenshot or measures a
  frame time after the session ends is measuring a process the mod still
  holds settings on. The blast radius is one game process, and the writes
  are each a single guarded assignment, so the risk is a stale setting
  outliving the reason for it, not a corrupted process.
- *Elevation of privilege*: none. These are process-local Unity settings
  the mod already holds authority to write; nothing outside the game process
  observes them.

## Mitigations present in code

| Control | Covers | Location |
|---|---|---|
| Control-character flattening of echoed env/argv (log-forging defense) | R3 | C#: LogText.SanitizeForLog (LogText.cs:65-79), the single implementation of the character rule (LogText.cs:18-40); every launch-context value ConnectTarget echoes goes through it (ConnectTarget.cs:68-69,98,159-160,249,283-284,315,321,350,462,479), as do EnvFlags.cs:63, both F1 console commands' echoes (ConsoleCmdConnect.cs:48,56, ConsoleCmdDiag.cs:124) and PlayerNames.cs:34. The console's own write point (ConsoleOutput.cs:29-45) does not sanitize, so the property depends on every call site flattening first; today both do. It covers the invisible-format characters and the U+2028/U+2029 separators a log reader lays out as a line break. Shell twin `sanitize_log_text` (scripts/log_sanitize.sh:27-42) flattens the same C1 and separator set and drops the same format characters; used at launch_client.sh:145,389,411, one_shot_join.sh:259,317 and zero_nre_join_loop.sh:226,230,234. Pinned by scripts/test_log_sanitize.sh, fuzzed over a seeded generator in scripts/test_log_sanitize_fuzz.sh, and behavioral tests in scripts/test_connect_target_parse.sh |
| Port range validation 1..65535 | malformed targets falling back to default port | ConnectTarget.cs:15-16,134-137. Shell twin `is_tcp_port` (scripts/config_validate.sh:66-75), read by one_shot_join.sh:82, zero_nre_join_loop.sh:225 and restart_pair.sh:49; `is_bounded_uint` (scripts/config_validate.sh:42-58) in the same file covers the seconds and attempt knobs, which reach `$(( ))` and `sleep(1)`. Both are fuzzed against the rule they state in scripts/test_config_validate.sh |
| Grammar normalization (scheme strip, bracketed IPv6, dangling colons) shared by console/env/argv paths | parser drift between entry points | ConnectTarget.cs:125-131,163-193,209-282; console reuses it (ConsoleCmdConnect.cs:39-40) |
| DNS timeout bound (5 s) | menu-thread freeze via wedged resolver | ConnectTarget.cs:299-354 |
| Connect-ready gate capped at 45 s monotonic | unbounded wait on a never-settling platform login | ModApi.cs:230-269; Source/ConnectMod/ConnectReady.cs |
| Automation gating of identity/auth Harmony patches | limits R1 to automation launches | `[AutomationPatch]` attribute (AutomationMode.cs:5-8) skipped unless enabled (ModApi.cs:70-72); gate auto-on only with a launch target or explicit env (AutomationMode.cs:17-24) |
| Local-host load patches scoped to non-automation Local sessions | R1-style identity/auth changes must not reach ordinary host play; the load wrapper is the one engine change that does (boundary 6) | LocalHostWorldLoadPatches.cs:51-53 |
| Bounded load waits (prefab prewarm 60 s, async drain 60 s) with logged timeout | silent startup hang | LocalHostWorldLoadPatches.cs:163-172,285-296 |
| Load-priority / force-sync restore in `finally` | leaving the client in a modified global state after a local-host load | LocalHostWorldLoadPatches.cs:72-84,123-133,306-334, engine-setting restore at :151-152. Scope: the local-host load wrapper only. The automation boot unblock and the diag-gated spawn heartbeat write the same settings with no restore (R7) |
| Hitch monitor started once per process | one eternal coroutine per hosted session | PerfTrace.cs:36-40 (latch at :21), started from LocalHostWorldLoadPatches.cs:160 |
| EULA gate handled once per process | a repeated `windowEula` request re-saving prefs and re-firing `MainMenuOpened` at every mod | EulaSkip.cs:19,50-72; pinned by scripts/test_eula_gate_once.sh |
| `platform.cfg` swap written to a temp file and renamed over the live config | a crash, signal, or full disk mid-write leaving an empty config under the client that reads it next | scripts/launch_client.sh:231-243; same reasoning for the backup copy at :212-221; pinned by scripts/test_launch_client_platform.py |
| Detached launcher and server stopped by owned process group, not by pid alone | orphaned Proton/mute-poller children stacking one per cycle, and the launcher's `platform.cfg` restore trap never running | `signal_owned` (scripts/one_shot_join.sh:185-197) checks liveness and signals the group only when the child leads it, used at one_shot_join.sh:242-251; pinned by scripts/test_one_shot_launcher_group.sh |
| EULA gate latch armed by the accept, not by the attempt | a failed prefs write latched as resolved, leaving the profile unwritten and the gate unretryable for the session | EulaSkip.cs:56-80; pinned by scripts/test_eula_gate_once.sh |
| Auto-join latch armed after the launch context resolves | a throwing resolution spending the session's one auto-join attempt, so no later menu open ever joins | ModApi.cs:193-231; pinned by scripts/test_auto_join_latch.sh |
| Staged mod folder emptied before staging | a reused stage root shipping files the payload no longer has | scripts/stage_mod.sh:70-75; pinned by scripts/test_stage_mod.sh |
| install/uninstall refuse an empty or root `MODS_DIR` | `rm -rf $(INSTALL_DIR)` on a top-level path built from an unset variable | Makefile:164-175,177-186; pinned by scripts/test_stage_mod.sh |
| Diagnostic traces gated behind `7DTD_CONNECT_DEBUG` / `diag on` | R6 log volume in normal play | the one gate, read first in every trace: DiagToggle.cs:19-47; consumers at WindowTrace.cs:20-30, SpawnedTrace.cs:12,36, AliveFlagsTrace.cs:16, SpawnStateHeartbeat.cs:19, LocalHostWorldLoadPatches.cs:113-121,155-159,233-235, LoadStateProbe.cs:14-20. This is a gate on *logging* only: it does not gate the engine-setting write at SpawnStateHeartbeat.cs:27, which happens after it (R7) |
| Heartbeats share one cadence (5 s) | two probes describing the same stall as two different ones, and independent tuning drifting apart | DiagToggle.cs:19, used by SpawnStateHeartbeat.cs:21; the spawn and load probes read the same load-gate math so neither can report a bar the other would hide (Source/ConnectMod/LoadGate.cs:10-40) |
| Guarded probes: every trace wraps its body in `try` and reports through a per-probe announce-once latch | a throwing diagnostic reaching the stock call site it was only observing, and a permanently dead probe reading as a healthy quiet join | ProbeFailure.cs:15-43; per-site `try` at WindowTrace.cs:25-30, SpawnedTrace.cs:21-27,45-49, AliveFlagsTrace.cs:27-35, SpawnStateHeartbeat.cs:44-49, BootUnblock.cs:243-251. The latch is keyed by probe name, so one dead probe cannot mute another's announcement (ProbeFailure.cs:5-15) |
| Player-name cap (24 code points, NFC, never cutting a surrogate pair), control/invisible-format flattening, and never-empty fallback | oversized/injected names reaching prefs; one name in two spellings, or a cap that corrupts an emoji name into U+FFFD | PlayerNames.cs:16-81, TextUtil.cs:22-85, ModApi.cs:122-169 |
| Copied evidence lines sanitized *and* prefixed `  log: ` before entering the control log | a server-supplied line reading as a harness marker, and a U+2028/C1 NEL inside one splitting it for a reader | scripts/join_evidence.sh:29-39; cap of 80 lines per cycle at :27; pinned by scripts/test_join_evidence.sh |
| `CYCLE` filename guard (safe charset, no leading dot) | path traversal in cycle artifact filenames | one_shot_join.sh:103-110; pinned by scripts/test_cycle_filename_guard.sh |
| Numeric guards on `PORT`/`TIMEOUT_SEC`/`SETTLE_SEC`/`WAIT_SECONDS` | regex/arithmetic skew from metacharacters | one_shot_join.sh:76-101, restart_pair.sh:36-52, mute_client_audio.sh:40-52 |
| One boolean table for every env flag (`env_bool`, the shell twin of `EnvFlags`): documented tokens only, an undocumented token read as on with a warning naming the variable and the value | a typo in a flag (`CLIENT_MUTE=ture`, `START_SERVER=true`) silently taking the opposite side of the opt-in/opt-out table | scripts/config_validate.sh:33-62; used by one_shot_join.sh:117 and launch_client.sh:117; pinned by scripts/test_config_validate.sh |
| Whitelists for `GFX_API` and `CLIENT_PLATFORM` | arbitrary strings becoming argv fragments or a config swap | launch_client.sh:96-104,140-147 |
| Disk-growth bounds in scratch dir | availability across repeated cycles | one_shot_join.sh:56-71 |
| Log-marker memoization contract (append-only assumption documented) | stale matches after truncation; repeated re-scan of a growing log | scripts/log_markers.sh:1-32 |
| CI least privilege + SHA-pinned actions; tag/version agreement gate | supply-chain injection via moved tags/actions | .github/workflows/ci.yml:15-23,30-41; .github/workflows/release.yml:18-45 |
| Byte-reproducible release archive | a rebuild of the same tree producing different bytes, so a consumer cannot tell a rebuilt zip from a substituted one | `repro_zip.sh` (scripts/repro_zip.sh:1-16,66-78): mtimes rewritten to `SOURCE_DATE_EPOCH`, `TZ=UTC`/`LC_ALL=C` pinned, explicit C-locale entry order, normalized modes, `zip -X` to drop uid/gid and extra fields; pinned by scripts/test_repro_zip.sh |
| Archive read back and compared against the staged payload | an archive that lost an entry, gained a build leftover, or is unreadable shipping as a release | scripts/package.sh:124-137 (sorted `unzip -Z1` vs `find`, both directions compared; a mismatch deletes the zip and fails) |
| Build record beside the archive: version, commit, dirty flag, epoch, dotnet version, sha256 of the zip | a consumer with no way to tell what they installed; every field written atomically through a temp file and renamed, and a missing digest tool fails the run rather than writing a blank sha256 | scripts/package.sh:139-172 |
| Release name cannot escape `dist/`, and a dirty tree cannot ship under a tag name | a tag or `VERSION=` override aiming the zip outside the output dir, or an artifact claiming a release while differing from it | scripts/package.sh:86-98 (`^[0-9A-Za-z][0-9A-Za-z._-]*$`); tag/`ModInfo.xml` agreement and the three-declaration version gate at .github/workflows/release.yml:31-57; pinned by scripts/test_version_sync.sh |
| R4's residual is not closed by any of the above | all of it is written by the same maintainer machine that compiled the payload and attached the zip, and the sha256 is self-asserted beside the artifact rather than signed or published anywhere the consumer can check independently | gap 4 |

Single points of failure: the R3 defense is one C# implementation
(`LogText.SanitizeForLog`) and one shell function (`sanitize_log_text`); the
tests pin each side separately but nothing pins their equivalence, and the two
do not agree on the invisible-format characters today. C# replaces each with a
space, preserving offsets and lengths (LogText.cs:73-76); the shell twin
deletes them outright (log_sanitize.sh:29-32) and flattens only control, C1 and
the two separators. Neither reaches a log in a form a reader lays out as a
second line, so forging stays closed, but the two sides are not the same rule
and a future change to one is not automatically a change to the other. The
console half of the same defense is weaker still: `ConsoleOutput.Emit`
(ConsoleOutput.cs:29-45) writes whatever string it is handed, so a future
F1 command that echoes operator input without `LogText.EchoForMessage`
forges as freely as the old pre-`ConsoleOutput` code did, and nothing in the
build fails on it. The build → runtime boundary has the same shape: the
reproducible zip, the archive-vs-stage comparison, the build record and the
sha256 are all produced by one maintainer machine in one `make package`
run, so a single compromised build host defeats all of them together, and
none of them is a control a consumer can run. The automation gate is the
only control separating normal play from all
identity/auth patches, and boundary 6 is the one place where a non-automation
code path deliberately reaches past it, so the local-host gate
(`IsNormalLocalHost`) is the single check standing between ordinary host play
and the load rewrite.

## Named gaps (unmitigated; ranked)

These are recorded, not fixed, here. Fixes belong to sec-review.

1. **R1 - intentional auth downgrade**: with automation on and no Steam/EOS
   login the client sends an empty ticket and a deterministic id derived by
   FNV-1a over `MachineName` (AuthFallbackPatches.cs:74-95). Predictable ids
   mean a peer who knows a victim's hostname knows its identity. Server-side
   authorization is the only thing standing; deployment guidance should say
   these clients belong on loopback/LAN test servers only.
2. **R2 - attacker-chosen connect target**: accepted by design (direct-connect
   tool). Residual: no warning surfaces when the target came from argv vs the
   operator's own env choice beyond the source label in the log.
3. **R6 - unbounded diagnostic output**: no rate limit, size cap, or
   auto-expiry on the window trace, the hitch monitor, the spawn traces or
   the flags trace (WindowTrace.cs:20-30, PerfTrace.cs:57-92,
   SpawnedTrace.cs:19,43, AliveFlagsTrace.cs:14-25). The spawn traces are
   the worst of them: one full managed stack trace per spawn event, with no
   cadence and no cap, into the log the harness copies into cycle evidence.
   A session left with `diag on` and a stalling or respawning renderer
   writes unbounded lines. Gating is the only control, and the gate is a
   local user toggle.
4. **R4 - release provenance, precisely**: the zip is byte-reproducible and
   carries a build record with its commit, dirty flag, toolchain version and
   sha256 (scripts/package.sh:139-172), and the archive is verified against
   the staged payload before it is written (package.sh:124-137). What is
   missing is any check a *consumer* can perform: the record is produced by
   the same maintainer machine that compiled the payload and the digest
   travels beside the artifact rather than over a signed or independently
   published channel, so a substituted zip and its substituted `.buildinfo`
   agree with each other. Consumers install a DLL into their game
   (Makefile:178-183) on trust alone. The workflow says this openly rather
   than claiming CI builds the artifact
   (.github/workflows/release.yml:8-12).
5. **R5 - broad kill patterns**: substring process matching can terminate
   unrelated processes (restart_pair.sh:109-114, one_shot_join.sh:162-167).
6. **R7 - unrestored engine settings**: two paths write process-global Unity
   settings and never put them back. The automation boot unblock writes four
   of them (BootUnblock.cs:62-66) and is at least mode-gated; the diagnostic
   spawn heartbeat writes `Application.runInBackground` (SpawnStateHeartbeat.cs:27)
   from a `diag on` toggle, in a path whose purpose is to observe the join
   and not to change it. Blast radius is one game process and the values are
   recoverable by a restart, so this is recorded as drift from the
   restore-in-`finally` rule the load wrapper follows, not as an escalation.
7. **No reporting channel**: `SECURITY.md` now states the preconditions, what
   the repository's version line actually supports (verifiable from the
   version-sync gate and the absence of any maintenance branch), and how to
   check a release artifact, and it says plainly that no disclosure address,
   advisory channel or security owner exists (it also says not to open a
   public issue for an unfixed one). What is still missing is an
   organizational decision this review does not make: a contact to report
   to, a private channel, and a named owner. Until then, a vulnerability
   report is a public GitHub issue by default, which is the wrong default
   for an unfixed one.

## Abuse cases (scenarios only; no attack demonstrated)

- **A1 - Redirected join**: a launcher shortcut or URL handler plants
  `-connect=<attacker>:<port>`; the operator sees the game boot normally and
  the mod auto-joins the attacker's host. Enabling path:
  `TryFromLaunchContext` (ConnectTarget.cs:222-277) → `OnMainMenuOpened`
  auto-join (ModApi.cs:175-225) → `TryConnect`. The log does name the source
  (ModApi.cs:204), which is the only tripwire.
- **A2 - Harness result forgery**: any local process able to append
  `Found own player entity with id` to the client log before the poller reads
  it flips the cycle verdict to `joined`. Enabling path: `log_seen`
  (log_markers.sh:60-104) reading `CLIENT_LOG_SRC` (one_shot_join.sh:135,325).
  Trusted implicitly; acceptable for a local test harness, but the model must
  say so.
- **A3 - Flag semantics abuse**: `EnvFlags.IsSetOn` treats any non-opt-out
  value as true (EnvFlags.cs:35-40), so garbage like `7DTD_CONNECT_DEBUG=x`
  enables verbose tracing (and R6's log volume). The unknown-token path does
  warn once per variable (EnvFlags.cs:60-65) and the shell twin has the same
  rule (scripts/config_validate.sh:33-62), so the value is not silent; it
  still fails toward ON, which for a diagnostic gate is the expensive side.
  Documented behavior, listed so nobody mistakes the warning for validation.
- **A4 - Diagnostic log flooding**: an operator debugging a stalling Local
  host leaves `diag on` on and walks away. The hitch monitor then writes a
  line per frame over 0.2 s for as long as the session lives
  (PerfTrace.cs:57-92), and a client that respawns adds a full managed stack
  trace per spawn event (SpawnedTrace.cs:19,43), filling the log the join
  harness scans. Enabling path: `diag on` (ConsoleCmdDiag.cs:99-101) →
  `DiagToggle.Set` (DiagToggle.cs:55-60) → the gated traces
  (`StartHitchMonitor` latch at PerfTrace.cs:36-40, the spawn setter prefix
  at SpawnedTrace.cs:10-20). Self-inflicted, and the harness cost is bounded
  by `log_seen`'s per-pattern offsets, but nothing in the code stops it.
- **A5 - Stale engine settings outlive their reason**: the same `diag on`
  leaves `Application.runInBackground` on for the rest of the process
  (SpawnStateHeartbeat.cs:27) even after the operator runs `diag off`,
  because the toggle gates the heartbeat, not the write it already made. A
  later session, or a screenshot/frame-time check the operator forgot about,
  observes a client still ticking unfocused. Enabling path:
  `diag on` → first heartbeat (`HeartbeatIntervalSec`, DiagToggle.cs:19) →
  the `runInBackground` assignment → `diag off` (ConsoleCmdDiag.cs:102-106)
  stops the heartbeat and leaves the setting.

## Response readiness (notes only)

- Audit trail: join provenance lives in client/lifecycle logs (echoed source
  labels, sanitized values); there is no separate audit stream. Log structure
  belongs to o11y-review.
- No documented path from "vulnerability reported" to "fix shipped" exists.
  `SECURITY.md` says so in the open rather than describing a process that
  does not exist; the path becomes real only once a contact and owner are
  decided (gap 7).
