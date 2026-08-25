# Threat Model — 7dtd-fastconnect

Client-only mod for 7 Days to Die: joins servers by IP without Steam
`steam://connect`, plus launch automation plumbing. This document is the
systemic view of what this repository's code can be attacked through, what it
puts at risk, and which mitigations exist in the code versus which are
missing. Individual vulnerabilities are not fixed here; each gap below names a
location and is handed to sec-review.

Scope: this repository only. The stock game client, the dedicated server
(zdtd / stock dedi), Steam/EOS netcode, and EAC are outside it; the mod
deliberately adds no S2C packet handling (AGENTS.md rules 4-5).

- Last reviewed: 2026-08-26 (against commit `1ee2c76`)
- Owner and review cadence are organizational decisions; none is assigned in
  this document.

## Risk-ranked summary

| # | Risk | Boundary | Status |
|---|---|---|---|
| R1 | Env-driven arbitrary file write: `7DTD_DUMP_BLOCK_IDS_PATH` names any path the client process can write | launch env → mod | No mitigation. Hand to sec-review |
| R2 | Auth downgrade under automation: empty auth ticket + synthetic platform identity let a Steam-less client join EAC-off LAN servers with a predictable identity | mod → server auth | Intentional by design; gated but worth an explicit deployment decision |
| R3 | Attacker-shapable `-connect=` text (e.g. a clicked `steam://run` URL) aims the client's outbound join at an attacker-chosen host | desktop → launch context → outbound network | Parse validation only; accepted residual by design |
| R4 | Log/marker forging via control characters in echoed env/argv values | launch context → client log → harness greps | Mitigated on both sides (`SanitizeForLog` twins); drift between the two implementations is the residual risk |
| R5 | Release zips are built on a maintainer machine and attached manually; no build provenance attestation | build → runtime | Noted; no claim exists that CI builds them |
| R6 | `pkill -9 -f '7DaysToDie'` / `pgrep -f` patterns match unrelated processes whose command line contains the substring | local operator tooling | No mitigation; local DoS blast radius |

The single highest-value correction for readers: nothing in this repo stores
secrets or listens on the network. The assets are the local machine, the
player identity the client presents to servers, and the trust harnesses place
in client-log markers.

## Assets

- **Local machine integrity**: game install and user config mutated by launch
  scripts (`platform.cfg` swap, scripts/launch_client.sh:93-130); diagnostic
  files written into the user profile (Source/ConnectMod/UserDirs.cs:14,
  Source/ConnectMod/BlockIdDump.cs:88).
- **Player identity**: the platform id and display name the client presents to
  servers. Under automation this can be synthetic and predictable
  (Source/ConnectMod/AuthFallbackPatches.cs:49-83,
  Source/ConnectMod/PlayerNames.cs:21-33) or env-selected
  (Source/ConnectMod/ModApi.cs:116-157).
- **Join-decision trust**: harnesses decide pass/fail by grepping client-log
  markers (scripts/one_shot_join.sh:98-101, scripts/log_markers.sh:43). The
  log is therefore an asset: forged markers forge results.
- **Consent state**: automation force-accepts the EULA and skips news/Discord
  gates (Source/ConnectMod/ModApi.cs:49-60, Source/ConnectMod/EulaSkip.cs).
- **Availability**: unattended launches must terminate; several bounded waits
  exist precisely so a wedged component cannot hang the machine's tooling.
- **Secrets**: none are stored, generated, or rotated by this repository.
  Steam/EOS credentials live entirely in the stock client; this mod only
  substitutes an *empty* ticket when no login exists
  (Source/ConnectMod/AuthFallbackPatches.cs:9-44,146-175).

## Entry points

Every entry point reads data from outside the process boundary:

| Entry point | Where parsed | Notes |
|---|---|---|
| Env `7DTD_CONNECT` | Source/ConnectMod/ConnectTarget.cs:179-194; scripts read it too (scripts/launch_client.sh:48, scripts/one_shot_join.sh:36, scripts/restart_pair.sh:68) | Attacker-shapable: a clicked `steam://run` URL chooses `-connect=` text (in-code rationale at ConnectTarget.cs:20-24) |
| argv `-connect=` / `+connect=` / `+connect_lobby` | ConnectTarget.cs:196-230 | Same threat model as the env var |
| F1 console `connect` / `7dtdconnect` / `joinip` | Source/ConnectMod/ConsoleCmdConnect.cs:8,27-51 | Local keyboard input; allowed in main menu (:23) |
| Env `7DTD_PLAYER_NAME` | ModApi.cs:118-137 | Sets `GamePrefs.PlayerName`, persisted to the client profile |
| Env `7DTD_CONNECT_AUTOMATION`, `7DTD_CONNECT_DEBUG`, `7DTD_CONNECT_FORCE_LOAD_SYNC` | Source/ConnectMod/AutomationMode.cs:17-24, Source/ConnectMod/DiagToggle.cs, Source/ConnectMod/LoadGate.cs | Boolean flags; truthiness shared in Source/ConnectMod/EnvFlags.cs:12-26 |
| Env `7DTD_DUMP_BLOCK_IDS`, `7DTD_DUMP_BLOCK_IDS_PATH` | BlockIdDump.cs:34,86-88,121 | Path value becomes a `File.WriteAllText` target (see R1) |
| Script env: `GAME`, `PROTON`, `COMPAT`, `STEAM_ROOT`, `GFX_API`, `CLIENT_MUTE*`, `CLIENT_PLATFORM`, `PORT`, `HOST`, `TIMEOUT_SEC`, `SETTLE_SEC`, `CYCLE`, `ZDTD_BIN`, `START_SERVER` | scripts/launch_client.sh:15-92, scripts/one_shot_join.sh:26-70, scripts/restart_pair.sh:14-24, scripts/mute_client_audio.sh:17-20 | Several are validated before use (see Mitigations); `GAME`/`PROTON`/`COMPAT` select executables by design |
| Client log file written by the game process | parsed by scripts/log_markers.sh:43-77 from one_shot_join.sh:115, copied at one_shot_join.sh:294, scanned by zero_nre_join_loop.sh:158 | Process-to-harness boundary; content is semi-trusted |
| Outbound network connect | ConnectTarget.cs:287-354 (`ConnectionManager.Connect`) after DNS resolution :238-284 | The only network traffic this repo initiates |

Deployment surface: GitHub Actions workflows run `make test` / coverage /
tag-gating with least privilege and SHA-pinned actions
(.github/workflows/ci.yml, .github/workflows/release.yml:18-28). The release
zip itself is built locally and attached manually
(.github/workflows/release.yml:8-12), so the pipeline never proves what ships.

## Trust boundaries and data flow

1. **Desktop integration → launch context** (env vars, argv): anything that
   can plant `7DTD_CONNECT` or a `-connect=` argument chooses where this
   client connects and what identity name it presents. Crossing point: the
   parsers above; sanitization at the log echo points only.
2. **Launch context → outbound network**: `TryParse` output feeds
   `ConnectionManager.Connect` directly (ConnectTarget.cs:305-343). There is
   no allow-list; any resolvable host:port is joined. Server responses are
   handled entirely by stock engine code; this mod adds no listener and no
   S2C parsing (AGENTS.md rule 5).
3. **Game process → shell harnesses**: lifecycle scripts treat the client log
   as evidence. Marker regexes decide `result=joined`
   (one_shot_join.sh:240-292). Anything able to write those bytes decides the
   verdict; the scripts assume only the game writes them.
4. **Scripts → on-disk config**: `CLIENT_PLATFORM=local` overwrites
   `$GAME/platform.cfg` after backing it up, restores on exit, self-heals a
   previous interrupted swap, and refuses when the backup cannot restore
   (launch_client.sh:96-130). Failure here silently changes which platform
   identity the user's next manual launch uses.
5. **Automation mode → engine internals**: Harmony patches tagged
   `[AutomationPatch]` replace auth-ticket production and platform identity
   (AuthFallbackPatches.cs) and are applied only when automation boot mode is
   on (ModApi.cs:66-83 skips them otherwise; gate detection at
   AutomationMode.cs:17-24 auto-enables whenever a launch target exists).

Privilege transitions: none. Everything runs as the desktop user; no service,
no setuid, no elevated installer (`make install` copies files into the game's
`Mods/` dir, Makefile:73-78).

## Threats per boundary

**Desktop → launch context (STRIDE)**

- *Spoofing/tampering*: crafted `-connect=` text redirects the join (R3);
  crafted control characters forge log lines and harness markers (R4).
- *Repudiation*: weak. Launch echoes do record source labels ("auto-join from
  7DTD_CONNECT=...", ModApi.cs:186; "Connect by IP ... (requested host=...)",
  ConnectTarget.cs:340), so the origin of a join is visible in the log.
- *Information disclosure*: the join handshake reveals player identity to
  whichever host the target names; block tables can be dumped to disk via
  `7DTD_DUMP_BLOCK_IDS*` (R1, R8-grade info disclosure).
- *DoS*: hostname resolution is bounded at 5 s (ConnectTarget.cs:248-255) and
  the auto-join ready-wait at 45 s monotonic (ModApi.cs:226-232); a wedged
  resolver cannot freeze the menu thread indefinitely.
- *Elevation of privilege*: none available; the mod runs entirely inside the
  game process with no additional authority.

**Launch context → outbound network**

- A hostile "server" receives the login packet containing the platform id and
  player name (synthetic ones included). Ticket material is either real
  (Steam/EOS logged in, patches pass through: AuthFallbackPatches.cs:16-31,
  150-163) or empty by construction. Impact of leaking a synthetic identity is
  low; impact of joining a hostile host is engine-level and out of scope here,
  but the redirect itself is this repo's decision (R3).

**Game process → shell harnesses**

- *Tampering*: forged success markers flip `result=joined` without a server
  (abuse case A2). Values the *mod* echoes are flattened first (R4
  mitigation); values other local processes write are trusted implicitly.

**Scripts → on-disk config / processes**

- *Tampering/availability*: the `platform.cfg` swap has backup, refuse-on-
  unrestorable, and self-heal paths (launch_client.sh:100-116); residual risk
  is losing the user's platform selection if both copies die mid-run.
- *Process targeting*: kill sweeps match substrings of any user's command line
  (`pkill -9 -f '7DaysToDie'`, restart_pair.sh:40; `pgrep -f
  '[/]7DaysToDie\.exe|wine64-preloader.*7DaysToDie'`, one_shot_join.sh:107),
  so an unrelated process whose argv mentions the string gets killed (R6).

## Mitigations present in code

| Control | Covers | Location |
|---|---|---|
| Control-character flattening of echoed env/argv (log-forging defense) | R4 | C#: ConnectTarget.SanitizeForLog (ConnectTarget.cs:25-38), used at :44-46,190,222-229,254,260,281,340,351 and ModApi.cs:145-151; shell twin `sanitize_log_text` (scripts/log_sanitize.sh:11-13) used at launch_client.sh:215,237 and one_shot_join.sh:175,230; pinned by scripts/test_log_sanitize.sh and behavioral tests in scripts/test_connect_target_parse.sh |
| Port range validation 1..65535 | malformed targets falling back to default port | ConnectTarget.cs:63-67 |
| Grammar normalization (scheme strip, bracketed IPv6, dangling colons) shared by console/env/argv paths | parser drift between entry points | ConnectTarget.cs:49-61,78-100; console reuses it (ConsoleCmdConnect.cs:36-41) |
| DNS timeout bound | menu-thread freeze via wedged resolver | ConnectTarget.cs:246-276 |
| Automation gating of identity/auth Harmony patches | limits R2 to automation launches | `[AutomationPatch]` attribute (AutomationMode.cs:6) skipped unless enabled (ModApi.cs:70-72); gate auto-on only with a launch target or explicit env (AutomationMode.cs:17-24) |
| Player-name length cap (24) and never-empty fallback | oversized/injected names reaching prefs | PlayerNames.cs:13-32, ModApi.cs:133-137 |
| `CYCLE` filename guard (safe charset, no leading dot) | path traversal in cycle artifact filenames | one_shot_join.sh:52-60; pinned by scripts/test_cycle_filename_guard.sh |
| Numeric guards on `PORT`/`TIMEOUT_SEC`/`SETTLE_SEC` | regex/arithmetic skew from metacharacters | one_shot_join.sh:30-51, restart_pair.sh:10-13, mute_client_audio.sh:17-20 |
| Whitelists for `GFX_API` and `CLIENT_PLATFORM` | arbitrary strings becoming argv fragments | launch_client.sh:64-71,90-92 |
| Disk-growth bounds in scratch dir | availability across repeated cycles | one_shot_join.sh:10-24 |
| Log-marker memoization contract (append-only assumption documented) | stale matches after truncation | scripts/log_markers.sh:1-40 |
| CI least privilege + SHA-pinned actions; tag/version agreement gate | supply-chain injection via moved tags/actions | .github/workflows/ci.yml:6,9-11,18,21-26; .github/workflows/release.yml:18-45 |

Single points of failure: the R4 defense rests entirely on the two
`SanitizeForLog` twins staying behaviorally identical; the tests pin each side
separately but nothing pins their equivalence. The automation gate is the only
control separating normal play from all identity/auth patches.

## Named gaps (unmitigated; ranked)

These are recorded, not fixed, here. Fixes belong to sec-review.

1. **R1 - env-controlled write path**: `7DTD_DUMP_BLOCK_IDS_PATH` is used
   verbatim as the dump destination (BlockIdDump.cs:86-88, File.WriteAllText
   at :121). Same trust level as `7DTD_CONNECT`, which the code itself calls
   attacker-shapable. Exploitability: needs env control, which R3 shows is
   plausible via desktop integration; impact is arbitrary-file overwrite with
   client privileges. Dump is opt-in via `7DTD_DUMP_BLOCK_IDS`
   (BlockIdDump.cs:34).
2. **R2 - intentional auth downgrade**: with automation on and no Steam/EOS
   login the client sends an empty ticket and a deterministic id derived by
   FNV-1a over `MachineName` (AuthFallbackPatches.cs:65-83). Predictable ids
   mean a peer who knows a victim's hostname knows its identity. Server-side
   authorization is the only thing standing; deployment guidance should say
   these clients belong on loopback/LAN test servers only.
3. **R3 - attacker-chosen connect target**: accepted by design (direct-connect
   tool). Residual: no warning surfaces when the target came from argv vs the
   operator's own env choice beyond the source label in the log.
4. **R5 - release provenance**: `make package` runs on a maintainer machine;
   the zip attached to releases carries no build attestation
   (.github/workflows/release.yml:8-12 states this openly). Consumers install
   a DLL into their game (Makefile:73-78) on trust alone.
5. **R6 - broad kill patterns**: substring process matching can terminate
   unrelated processes (restart_pair.sh:35-41, one_shot_join.sh:103-108).
6. **No SECURITY.md**: the repository has no disclosure contact, supported-
   versions statement, or security-policy claims. Nothing here contradicts
   reality because nothing is claimed; creating one requires organizational
   decisions (contact, process) that this review does not invent.

## Abuse cases (scenarios only; no attack demonstrated)

- **A1 - Redirected join**: a launcher shortcut or URL handler plants
  `-connect=<attacker>:<port>`; the operator sees the game boot normally and
  the mod auto-joins the attacker's host. Enabling path:
  `TryFromLaunchContext` (ConnectTarget.cs:196-230) → `OnMainMenuOpened`
  auto-join (ModApi.cs:173-213) → `TryConnect`. The log does name the source
  (ModApi.cs:186), which is the only tripwire.
- **A2 - Harness result forgery**: any local process able to append
  `Found own player entity with id` to the client log before the poller reads
  it flips the cycle verdict to `joined`. Enabling path: `log_seen`
  (log_markers.sh:43-77) reading `CLIENT_LOG_SRC` (one_shot_join.sh:115).
  Trusted implicitly; acceptable for a local test harness, but the model must
  say so.
- **A3 - Flag semantics abuse**: `EnvFlags.IsSetOn` treats any non-opt-out
  value as true (EnvFlags.cs:23-26), so garbage like `7DTD_DUMP_BLOCK_IDS=x`
  enables the dump rather than failing loudly. Documented behavior, listed so
  nobody mistakes it for validation.

## Response readiness (notes only)

- Audit trail: join provenance lives in client/lifecycle logs (echoed source
  labels, sanitized values); there is no separate audit stream. Log structure
  belongs to o11y-review.
- No documented path from "vulnerability reported" to "fix shipped" exists
  (follows from the missing SECURITY.md, gap 6).
