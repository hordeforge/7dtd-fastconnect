# Threat Model: 7dtd-fastconnect

Client-only mod for 7 Days to Die: joins servers by IP without Steam
`steam://connect`, plus launch automation plumbing. This document is the
systemic view of what this repository's code can be attacked through, what it
puts at risk, and which mitigations exist in the code versus which are
missing. Individual vulnerabilities are not fixed here; each gap below names a
location and is handed to sec-review.

Scope: this repository only. The stock game client, the dedicated server
(zdtd / stock dedi), Steam/EOS netcode, and EAC are outside it; the mod
deliberately adds no S2C packet handling (AGENTS.md rules 4-5).

- Last reviewed: 2026-09-28 (against the v0.12.0 tree)
- Owner and review cadence are organizational decisions; none is assigned in
  this document.

## Risk-ranked summary

| # | Risk | Boundary | Status |
|---|---|---|---|
| R1 | Auth downgrade under automation: empty auth ticket + synthetic platform identity let a Steam-less client join EAC-off LAN servers with a predictable identity | mod → server auth | Intentional by design; gated but worth an explicit deployment decision |
| R2 | Attacker-shapable `-connect=` text (e.g. a clicked `steam://run` URL) aims the client's outbound join at an attacker-chosen host | desktop → launch context → outbound network | Parse validation only; accepted residual by design |
| R3 | Log/marker forging via control characters and Unicode line separators in echoed env/argv values | launch context → client log → harness greps | Mitigated on both sides (`SanitizeForLog` twins); drift between the two implementations is the residual risk |
| R4 | Release zips are built on a maintainer machine and attached manually; no build provenance attestation | build → runtime | Noted; no claim exists that CI builds them |
| R5 | `pkill -9 -f '7DaysToDie'` / `pgrep -f` patterns match unrelated processes whose command line contains the substring | local operator tooling | No mitigation; local DoS blast radius |
| R6 | Verbose diagnostics are unbounded: `diag on` makes the window trace emit per open/close and the hitch monitor emit per slow frame for the process lifetime, into the same log the join harnesses grep | local user → client log → harness/disk | Gated behind a local opt-in and a frame threshold; no volume cap |

Risks are renumbered from the previous revision. The former R1, an
env-controlled `File.WriteAllText` destination via `7DTD_DUMP_BLOCK_IDS_PATH`,
is closed in v0.11.0: the block-id dumper that owned that write path was
removed, so the mod writes no files at all. R6 was added in v0.12.0, when the
diagnostic traces and the local-host hitch monitor grew past the boot
heartbeat that was there before.

The single highest-value correction for readers: nothing in this repo stores
secrets or listens on the network. The assets are the local machine, the
player identity the client presents to servers, and the trust harnesses place
in client-log markers.

## Assets

- **Local machine integrity**: game install and user config mutated by launch
  scripts (`platform.cfg` swap, scripts/launch_client.sh:151-230) and by the
  mod's own prefs writes (player name, EULA acceptance, Discord and intro
  prefs, Source/ConnectMod/ModApi.cs:25-60,116-156). The mod writes no
  arbitrary file: the block-id dumper that did was removed in v0.11.0.
- **Player identity**: the platform id and display name the client presents to
  servers. Under automation this can be synthetic and predictable
  (Source/ConnectMod/AuthFallbackPatches.cs:49-95,
  Source/ConnectMod/PlayerNames.cs:36-49) or env-selected
  (Source/ConnectMod/ModApi.cs:116-156).
- **Join-decision trust**: harnesses decide pass/fail by grepping client-log
  markers (scripts/one_shot_join.sh:128,270-312; scripts/log_markers.sh:43).
  The log is therefore an asset: forged markers forge results, and a log the
  mod fills with diagnostics is a log the harness must still scan.
- **Consent state**: automation force-accepts the EULA and skips news/Discord
  gates (Source/ConnectMod/ModApi.cs:49-60, Source/ConnectMod/EulaSkip.cs:15-24).
- **Session stability**: a Local host that hangs at "Initializing world" is
  the failure the local-host load patches exist to remove, so holding sync
  loading and the raised load priority must be released when startup ends
  (Source/ConnectMod/LocalHostWorldLoadPatches.cs:116-128,344-363).
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
| Env `7DTD_CONNECT` | Source/ConnectMod/ConnectTarget.cs:294-303; scripts read it too (scripts/launch_client.sh:90,326,348; scripts/one_shot_join.sh:76,215,270; scripts/restart_pair.sh:134) | Attacker-shapable: a clicked `steam://run` URL chooses `-connect=` text (in-code rationale at ConnectTarget.cs:56-69 and LogText.cs:12-18) |
| argv `-connect=` / `+connect=` / `+connect_lobby` | ConnectTarget.cs:305-338 | Same threat model as the env var |
| F1 console `connect` / `7dtdconnect` / `joinip` | Source/ConnectMod/ConsoleCmdConnect.cs:8,27-51 | Local keyboard input; allowed in main menu (:23) |
| F1 console `diag` / `7dtd_diag` / `zdiag` | Source/ConnectMod/ConsoleCmdDiag.cs:8,20-52 | Flips verbose traces at runtime; the console override outranks the env snapshot (Source/ConnectMod/DiagToggle.cs:19-47) |
| Env `7DTD_PLAYER_NAME` | ModApi.cs:118-135 | Sets `GamePrefs.PlayerName`, persisted to the client profile |
| Env `7DTD_CONNECT_AUTOMATION`, `7DTD_CONNECT_DEBUG`, `7DTD_CONNECT_FORCE_LOAD_SYNC` | Source/ConnectMod/AutomationMode.cs:12-24, Source/ConnectMod/DiagToggle.cs:5, Source/ConnectMod/BootUnblock.cs | Boolean flags; truthiness shared in Source/ConnectMod/EnvFlags.cs:12-38 |
| Script env: `GAME`, `PROTON`, `COMPAT`, `STEAM_ROOT`, `GFX_API`, `CLIENT_MUTE_TIMEOUT`, `CLIENT_PLATFORM`, `PORT`, `HOST`, `TIMEOUT_SEC`, `SETTLE_SEC`, `CYCLE`, `SCRATCH`, `ZDTD_BIN`, `START_SERVER`, `WINEDLLOVERRIDES` | scripts/launch_client.sh:57-157,324; scripts/one_shot_join.sh:65-98; scripts/restart_pair.sh:36-52; scripts/mute_client_audio.sh:40-52 | Several are validated before use (see Mitigations); `GAME`/`PROTON`/`COMPAT` select executables by design. `WINEDLLOVERRIDES` is appended to, not overwritten (launch_client.sh:324), so anything already in the operator's env also reaches the Wine process |
| Hardcoded process args added by the launcher | scripts/launch_client.sh:92 (`-skipintro -SkipNewsScreen=true -disablenativeinput`) | Not operator input, but they change what the client loads; `-disablenativeinput` was added for Proton boot stability, not for a security control |
| Client log file written by the game process | parsed by scripts/log_markers.sh:51-109 from one_shot_join.sh:281-320, copied at one_shot_join.sh:334, rescanned by zero_nre_join_loop.sh:210,216-221 | Process-to-harness boundary; content is semi-trusted |
| Outbound network connect | ConnectTarget.cs:404-469 (`ConnectionManager.Connect` at :460) after DNS resolution :347-399 | The only network traffic this repo initiates |
| Engine load pipeline (Local host) | Source/ConnectMod/LocalHostWorldLoadPatches.cs:414-430 patches `World.LoadWorld`, `GameManager.createWorld`, `GameManager.StartAsServer` | No external input; the wrapping coroutines read engine internals by reflection (:365-392) and change global load settings (:72-84,344-363) |

Deployment surface: GitHub Actions workflows run `make test` / coverage /
tag-gating with least privilege and SHA-pinned actions
(.github/workflows/ci.yml:15-23,30-41, .github/workflows/release.yml:18-29).
The release zip itself is built locally and attached manually
(.github/workflows/release.yml:8-12), so the pipeline never proves what ships.

## Trust boundaries and data flow

1. **Desktop integration → launch context** (env vars, argv): anything that
   can plant `7DTD_CONNECT` or a `-connect=` argument chooses where this
   client connects and what identity name it presents. Crossing point: the
   parsers above; sanitization at the log echo points only.
2. **Launch context → outbound network**: `TryParse` output feeds
   `ConnectionManager.Connect` directly (ConnectTarget.cs:288-342). There is
   no allow-list; any resolvable host:port is joined. Server responses are
   handled entirely by stock engine code; this mod adds no listener and no
   S2C parsing (AGENTS.md rule 5).
3. **Game process → shell harnesses**: lifecycle scripts treat the client log
   as evidence. Marker regexes decide `result=joined`
   (one_shot_join.sh:270-312). Anything able to write those bytes decides the
   verdict; the scripts assume only the game writes them.
4. **Scripts → on-disk config**: `CLIENT_PLATFORM=local` overwrites
   `$GAME/platform.cfg` after backing it up, restores on exit, self-heals a
   previous interrupted swap, refuses when the backup cannot restore, and holds
   an exclusive lock (`platform.cfg.re-local.lock`) so a second launcher on the
   same install reuses that swap instead of taking the single backup slot
   (launch_client.sh:151-230). Failure here silently changes which platform
   identity the user's next manual launch uses.
5. **Automation mode → engine internals**: Harmony patches tagged
   `[AutomationPatch]` replace auth-ticket production and platform identity
   (AuthFallbackPatches.cs) and are applied only when automation boot mode is
   on (ModApi.cs:62-86 skips them otherwise; gate detection at
   AutomationMode.cs:17-24 auto-enables whenever a launch target exists).
6. **Local host session → engine load pipeline** (new in v0.12.0): the
   local-host world-load patches are *not* automation-gated; they activate on
   any non-automation Local server session
   (LocalHostWorldLoadPatches.cs:51-53). The mod therefore rewrites engine
   coroutines, raises `Application.backgroundLoadingPriority` and
   `runInBackground`, and holds sync addressable loading through a private
   static field (LocalHostWorldLoadPatches.cs:344-363) in ordinary host
   play. Restores are in a `finally` (:116-128); a hard process kill mid-load
   leaves nothing to restore because the state dies with the process.
7. **Local user → client log** (new in v0.12.0): `diag on` or
   `7DTD_CONNECT_DEBUG` turns on per-window traces
   (Source/ConnectMod/WindowTrace.cs:14-24), the spawn/load heartbeats
   (Source/ConnectMod/LoadStateProbe.cs:11-15) and the Local-host hitch
   monitor, which runs for the whole process lifetime and logs every frame
   over 0.2 s (LocalHostWorldLoadPatches.cs:177-230).

Privilege transitions: none. Everything runs as the desktop user; no service,
no setuid, no elevated installer (`make install` copies files into the game's
`Mods/` dir, Makefile:84-90).

## Threats per boundary

**Desktop → launch context (STRIDE)**

- *Spoofing/tampering*: crafted `-connect=` text redirects the join (R2);
  crafted control characters forge log lines and harness markers (R3).
- *Repudiation*: weak. Launch echoes do record source labels ("auto-join from
  7DTD_CONNECT=...", ModApi.cs:201; "Connect by IP ... (requested host=...)",
  ConnectTarget.cs:455), so the origin of a join is visible in the log.
- *Information disclosure*: the join handshake reveals player identity to
  whichever host the target names. Nothing else leaves the process: the mod
  writes no files and opens no listener.
- *DoS*: hostname resolution is bounded at 5 s (ConnectTarget.cs:355-363) and
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
- *DoS*: a diagnostic flood (R6) grows the file the poller greps; the
  memoized matcher bounds the re-scan cost per pattern, but the first pass
  over a multi-megabyte log and the harness's overall time budget are both
  spent on mod output.

**Scripts → on-disk config / processes**

- *Tampering/availability*: the `platform.cfg` swap has backup, refuse-on-
  unrestorable, and self-heal paths (launch_client.sh:165-230), and is exclusive
  across concurrent launchers via `flock`; residual risk is losing the user's
  platform selection if both copies die mid-run.
- *Process targeting*: kill sweeps match substrings of any user's command line
  (`pkill -9 -f '7DaysToDie'`, restart_pair.sh:91; `pgrep -f
  '[/]7DaysToDie.exe|wine64-preloader.*7DaysToDie'`, one_shot_join.sh:137),
  so an unrelated process whose argv mentions the string gets killed (R5).

**Local host session → engine load pipeline**

- *Tampering*: the wrapper decides which coroutine yields reach Unity, and a
  wrong frame count (nine, LocalHostWorldLoadPatches.cs:31) changes world
  creation semantics. The count is a fixed property of the game build, so a
  game update is the trigger, not an attacker.
- *Denial of service*: a wedged LoadManager is bounded (prefab prewarm 60 s,
  :141-170; async drain 60 s, :315-323) and the drain result is logged, so a
  silent hang is not possible; but the sync-loading hold is released only
  from `Flatten`'s `finally` (:116-128, :357-363). Losing that release leaves
  the client in forced sync loading for the rest of the session.
- *Information disclosure*: the raised load priority and `runInBackground`
  are process-local Unity settings, restored in the same `finally`; no data
  leaves the machine.

**Local user → client log**

- *DoS*: unbounded diagnostic output (R6). The window trace is documented as
  spamming every tick in normal play (WindowTrace.cs:9-12), and the hitch
  monitor logs every frame over 0.2 s forever. Both need the local user to
  opt in, so this is self-inflicted, but the log is shared with the harness
  that grades joins.
- *Spoofing*: diagnostic lines share the client log with harness markers; the
  mod's own echoes are sanitized (R3), and its diagnostic lines carry a fixed
  `[7dtd-fastconnect] ` prefix, so they do not contain marker text.

## Mitigations present in code

| Control | Covers | Location |
|---|---|---|
| Control-character flattening of echoed env/argv (log-forging defense) | R3 | C#: LogText.SanitizeForLog (LogText.cs:19-32), the control-only leaf used by EnvFlags.cs:63 and the console echo path; ConnectTarget.SanitizeForLog (ConnectTarget.cs:70-83) is the stricter twin every launch-context value this module echoes goes through (ConnectTarget.cs:111-112,202-203,296,329-330,360,366,395,455,466) and PlayerNames.cs:34: it adds the invisible-format characters the leaf leaves alone and the U+2028/U+2029 separators a log reader lays out as a line break. Shell twin `sanitize_log_text` (scripts/log_sanitize.sh:27-42) flattens the same C1 and separator set and drops the same format characters; used at launch_client.sh:157,326,348 and one_shot_join.sh:215,270. Pinned by scripts/test_log_sanitize.sh and behavioral tests in scripts/test_connect_target_parse.sh |
| Port range validation 1..65535 | malformed targets falling back to default port | ConnectTarget.cs:15-16,134-137 |
| Grammar normalization (scheme strip, bracketed IPv6, dangling colons) shared by console/env/argv paths | parser drift between entry points | ConnectTarget.cs:125-131,163-193,209-282; console reuses it (ConsoleCmdConnect.cs:36-41) |
| DNS timeout bound (5 s) | menu-thread freeze via wedged resolver | ConnectTarget.cs:347-399 |
| Connect-ready gate capped at 45 s monotonic | unbounded wait on a never-settling platform login | ModApi.cs:230-269; Source/ConnectMod/ConnectReady.cs |
| Automation gating of identity/auth Harmony patches | limits R1 to automation launches | `[AutomationPatch]` attribute (AutomationMode.cs:5-8) skipped unless enabled (ModApi.cs:70-72); gate auto-on only with a launch target or explicit env (AutomationMode.cs:17-24) |
| Local-host load patches scoped to non-automation Local sessions | R1-style identity/auth changes must not reach ordinary host play; the load wrapper is the one engine change that does (boundary 6) | LocalHostWorldLoadPatches.cs:51-53 |
| Bounded load waits (prefab prewarm 60 s, async drain 60 s) with logged timeout | silent startup hang | LocalHostWorldLoadPatches.cs:156-167,315-333 |
| Load-priority / force-sync restore in `finally` | leaving the client in a modified global state | LocalHostWorldLoadPatches.cs:116-128,344-363 |
| Hitch monitor started once per process | one eternal coroutine per hosted session | LocalHostWorldLoadPatches.cs:177-184 |
| EULA gate handled once per process | a repeated `windowEula` request re-saving prefs and re-firing `MainMenuOpened` at every mod | EulaSkip.cs:17-19,50,72; pinned by scripts/test_eula_gate_once.sh |
| Staged mod folder emptied before staging | a reused stage root shipping files the payload no longer has | scripts/stage_mod.sh:69-74; pinned by scripts/test_stage_mod.sh |
| Diagnostic traces gated behind `7DTD_CONNECT_DEBUG` / `diag on` | R6 log volume in normal play | DiagToggle.cs:5,19-30; WindowTrace.cs:14-17; LocalHostWorldLoadPatches.cs:212,233-237; LoadStateProbe.cs:11-15 |
| Announce-once probe failures | a dead probe reading as a healthy quiet join | ProbeFailure.cs:15-43 |
| Player-name cap (24 code points, NFC, never cutting a surrogate pair), control/invisible-format flattening, and never-empty fallback | oversized/injected names reaching prefs; one name in two spellings, or a cap that corrupts an emoji name into U+FFFD | PlayerNames.cs:16-81, TextUtil.cs:22-85, ModApi.cs:122-169 |
| `CYCLE` filename guard (safe charset, no leading dot) | path traversal in cycle artifact filenames | one_shot_join.sh:92-100; pinned by scripts/test_cycle_filename_guard.sh |
| Numeric guards on `PORT`/`TIMEOUT_SEC`/`SETTLE_SEC`/`WAIT_SECONDS` | regex/arithmetic skew from metacharacters | one_shot_join.sh:65-92, restart_pair.sh:36-52, mute_client_audio.sh:40-52 |
| Whitelists for `GFX_API` and `CLIENT_PLATFORM` | arbitrary strings becoming argv fragments or a config swap | launch_client.sh:105-113,152-159 |
| Disk-growth bounds in scratch dir | availability across repeated cycles | one_shot_join.sh:45-60 |
| Log-marker memoization contract (append-only assumption documented) | stale matches after truncation; repeated re-scan of a growing log | scripts/log_markers.sh:1-40 |
| CI least privilege + SHA-pinned actions; tag/version agreement gate | supply-chain injection via moved tags/actions | .github/workflows/ci.yml:15-23,30-41; .github/workflows/release.yml:18-45 |

Single points of failure: the R3 defense rests entirely on the two
`SanitizeForLog` twins staying behaviorally identical; the tests pin each side
separately but nothing pins their equivalence. The automation gate is the only
control separating normal play from all identity/auth patches, and boundary 6
is the one place where a non-automation code path deliberately reaches past
it, so the local-host gate (`IsNormalLocalHost`) is the single check standing
between ordinary host play and the load rewrite.

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
   auto-expiry on the window trace or the hitch monitor
   (WindowTrace.cs:14-24, LocalHostWorldLoadPatches.cs:201-230). A session
   left with `diag on` and a stalling renderer writes unbounded lines into the
   same log the join harness greps. Gating is the only control, and the gate
   is a local user toggle.
4. **R4 - release provenance**: `make package` runs on a maintainer machine;
   the zip attached to releases carries no build attestation
   (.github/workflows/release.yml:8-12 states this openly). Consumers install
   a DLL into their game (Makefile:84-90) on trust alone.
5. **R5 - broad kill patterns**: substring process matching can terminate
   unrelated processes (restart_pair.sh:86-91, one_shot_join.sh:133-138).
6. **No SECURITY.md**: the repository has no disclosure contact, supported-
   versions statement, or security-policy claims. Nothing here contradicts
   reality because nothing is claimed; creating one requires organizational
   decisions (contact, process) that this review does not invent.

## Abuse cases (scenarios only; no attack demonstrated)

- **A1 - Redirected join**: a launcher shortcut or URL handler plants
  `-connect=<attacker>:<port>`; the operator sees the game boot normally and
  the mod auto-joins the attacker's host. Enabling path:
  `TryFromLaunchContext` (ConnectTarget.cs:288-342) → `OnMainMenuOpened`
  auto-join (ModApi.cs:172-222) → `TryConnect`. The log does name the source
  (ModApi.cs:201), which is the only tripwire.
- **A2 - Harness result forgery**: any local process able to append
  `Found own player entity with id` to the client log before the poller reads
  it flips the cycle verdict to `joined`. Enabling path: `log_seen`
  (log_markers.sh:43-78) reading `CLIENT_LOG_SRC` (one_shot_join.sh:109,271).
  Trusted implicitly; acceptable for a local test harness, but the model must
  say so.
- **A3 - Flag semantics abuse**: `EnvFlags.IsSetOn` treats any non-opt-out
  value as true (EnvFlags.cs:23-26), so garbage like `7DTD_CONNECT_DEBUG=x`
  enables verbose tracing (and R6's log volume) rather than failing loudly.
  Documented behavior, listed so nobody mistakes it for validation.
- **A4 - Diagnostic log flooding**: an operator debugging a stalling Local
  host leaves `diag on` on and walks away. The hitch monitor then writes a
  line per frame over 0.2 s for as long as the session lives
  (LocalHostWorldLoadPatches.cs:206-229), filling the log the join harness
  scans. Enabling path: `diag on` (ConsoleCmdDiag.cs:30-35) → `DiagToggle.Set`
  (DiagToggle.cs:40-47) → `HitchMonitor` loop (:201-230). Self-inflicted, and
  the harness cost is bounded by `log_seen`'s per-pattern offsets, but nothing
  in the code stops it.

## Response readiness (notes only)

- Audit trail: join provenance lives in client/lifecycle logs (echoed source
  labels, sanitized values); there is no separate audit stream. Log structure
  belongs to o11y-review.
- No documented path from "vulnerability reported" to "fix shipped" exists
  (follows from the missing SECURITY.md, gap 6).
