# ⚡ Hotwire (7DTD FastConnect)

> **Part of [HordeForge](https://github.com/hordeforge)**: High-Performance Systems Engineering for 7 Days to Die.

![CI](https://github.com/hordeforge/7dtd-fastconnect/actions/workflows/ci.yml/badge.svg)
![coverage](https://raw.githubusercontent.com/hordeforge/7dtd-fastconnect/badges/coverage.svg)
![license](https://img.shields.io/github/license/hordeforge/7dtd-fastconnect)
![release](https://img.shields.io/github/v/release/hordeforge/7dtd-fastconnect)
![languages](https://img.shields.io/github/languages/count/hordeforge/7dtd-fastconnect)
![top language](https://img.shields.io/github/languages/top/hordeforge/7dtd-fastconnect)

Tiny **client** helper for joining local/dev servers (especially **zdtd-server**) without Steam `steam://connect`, plus automation hooks for automated join tests.

Steam connect fails for zdtd (`app id specified by server is invalid`) because zdtd is not a Steam Game Server. This mod calls the same path as **Connect to IP**.

**Scope (v0.9+):** connect / auto-join / skip news+EULA+Discord for headless testing only.
Missing terrain, signs, inventory, spawn, or deco behaviour is fixed on the **server**
(zdtd), never by inventing world state in this mod.

**Gameplay automation** (dig/place/suites, scored exit codes) lives in sibling
[`../7dtd-playtest/`](../7dtd-playtest/). Install both mods for automated playtests.

CI runs `make test` on every push and PR. Packaged builds are attached to
GitHub releases (`make package` produces `dist/7dtd-fastconnect-<tag>.zip`).
Release notes and upgrade notes: [CHANGELOG.md](CHANGELOG.md). How to cut one,
and how to roll one back: [docs/RELEASING.md](docs/RELEASING.md).

**Versioning:** the project is `0.x`. A minor bump (`0.Y.0`) may change
behavior, including the launcher and harness CLI and the environment variables
the mod reads; a patch bump fixes behavior and adds no contract change. Every
release section in the changelog carries its own breaking notes when there are
any, and no version is re-tagged.

## Requirements

Running the tests needs no game install and no network; the tools below are all
it takes. `make doctor` checks them and names whatever is missing with the
command that installs it:

```bash
make doctor
```

- `shellcheck`, `zip`, `unzip`, `jq` from the system packages
  (Debian/Ubuntu: `apt-get install shellcheck zip unzip jq`)
- the dotnet SDK band pinned in `global.json` (`dotnet --version` must satisfy
  it), or `mcs` + `mono` for the same C# gate
- [`uv`](https://docs.astral.sh/uv/) for the pinned Python gates:
  `uv sync --frozen --group dev` installs ruff, mypy, pytest, and yamllint at
  the exact versions in `pyproject.toml` / `uv.lock`. Without uv the gates fall
  back to those tools on `PATH`, each held to the same pin by
  `scripts/assert_tool_pin.sh`.

Everything else is a game-side requirement:

- Stock client **EAC off** (`-noeac`; C# mods require it)
- `0_TFP_Harmony` present (stock)
- Game at `~/.local/share/Steam/steamapps/common/7 Days To Die` (override with `GAME=`)
- dotnet SDK 8 for `make build` / `make package` (band pinned by `global.json`,
  package graph pinned by `Source/ConnectMod/packages.lock.json`, restore
  sources declared in `NuGet.config`; the build
  restores in locked mode, so a dependency bump needs an explicit
  `dotnet restore Source/ConnectMod/ConnectMod.csproj -p:RestorePackagesWithLockFile=true`)
- `zip` for `make package` (archive bytes are reproducible; `SOURCE_DATE_EPOCH` defaults to the last commit)

`make build` is path-independent: the mod is compiled with debug symbols off
and deterministic codegen, so the dll a checkout produces does not name the
directory it was built in. `make package` is byte-reproducible on top of that
(`scripts/repro_zip.sh` pins entry order, mtimes, permission bits, and
uid/gid) and drops a
`dist/7dtd-fastconnect-<version>.buildinfo` beside the archive recording the
commit, SDK version, epoch, and sha256.

## Skips (faster boot)

| Screen | How |
|---|---|
| TFP intro splash video | Process arg **`-skipintro`** (must be on argv; splash runs before mods) |
| News “click to continue” | **`-SkipNewsScreen=true`** + Harmony forces `shownNewsScreenOnce` / blocks `XUiC_NewsScreen.Open` |
| InControl native input | Process arg **`-disablenativeinput`**. V 3.2.0 turns native input on by default; that plugin crashes Proton before mods load |
| Proton XInput | `WINEDLLOVERRIDES` disables `xinput1_3.dll`, `xinput1_4.dll`, `xinput9_1_0.dll`. InControl still calls `XInputGetState` after `-disablenativeinput`; Proton's stub crashes a Steam-free Local client |
| EULA accept gate | Harmony forces the `HasAcceptedLatestEula` getter true and blocks the `windowEula` window: `EulaLatestVersion` / `EulaVersionAccepted` are written and saved, then the main menu is reopened |
| Opener movie on world load | `showOpenerMovieOnLoad = false`, `OptionsIntroMovieEnabled = false` |
| Discord login / SDK | `GamePrefs.DiscordDisabled=true` + Harmony skips `DiscordManager.Init` and Discord first-time menu |

`scripts/launch_client.sh` always adds `-skipintro -SkipNewsScreen=true -disablenativeinput`.

## Install

```bash
cd 7dtd-fastconnect
make install
```

Installs to `$GAME/Mods/7dtd-fastconnect/`.

From a release zip instead: unzip `7dtd-fastconnect-<version>.zip` into
`$GAME/Mods/`. The archive already contains the `7dtd-fastconnect/` folder at
its top level and holds only `7dtd-fastconnect.dll`, `ModInfo.xml`, and
`LICENSE`, so it lands where the game looks for mods.

## Tests

```bash
make help     # list the targets
make doctor   # the tools `make test` needs, and what is missing here
make test     # every gate CI runs: the full local verification
```

One gate at a time, for the edit-test loop. `make gate` takes a shell gate or a
Python gate; a `.py` gate goes to pytest, never straight to the interpreter, so
it cannot report a green that ran nothing:

```bash
make gate GATE=scripts/test_log_sanitize.sh
make gate GATE=scripts/test_launch_client_platform.py
make gate GATE=scripts/test_launch_client_platform.py GATE_ARGS=-kresolve_compat
```

The whole suite is offline: no game install, no server, no audio daemon.
`test_connect_target_parse.sh` is behavioral rather than structural: it
compiles the real `Source/ConnectMod/ConnectTarget.cs` with `mcs`+mono (falling
back to the dotnet SDK pinned by `global.json`) against compiler-only game-API
stubs (`scripts/testdata/`) and runs target parsing and launch-context
resolution for real. It skips itself only when neither toolchain is present.
`test_repro_zip.sh` pins the byte-reproducibility contract of packaging.
The shellcheck / yamllint / ruff / mypy / pytest gates run last. All five are
mandatory, so a missing toolchain fails the run instead of reporting a green
that never analyzed anything. With `uv` on PATH the Python gates run the
versions pinned in `pyproject.toml` and hash-checked in `uv.lock`; without it
they fall back to the tools on PATH, which must report the same pinned version
or the gate stops (`scripts/assert_tool_pin.sh`). A tool missing from the
system (zip, jq, a C# compiler) is named before the first gate runs
(`scripts/check_prereqs.sh`), so those gates cannot report a pass they never
took.

## Usage

### F1 console (main menu)

```text
connect 127.0.0.1
connect 127.0.0.1 27025
connect 127.0.0.1:27025
```

Aliases: `7dtdconnect`, `joinip`. Default port **27025** (zdtd ServerPort / Connect-to-IP port).

A rejected target echoes the argument the console read and names the fix
(`port must be a number from 1 to 65535`, `missing host; expected host[:port]`),
so a typo needs no second guess about which half of the input was wrong.

### Auto-join on main menu

**Environment (preferred):**

```bash
# canonical: set via `env` (bash cannot assign/export names starting with a digit)
env 7DTD_CONNECT=127.0.0.1:27025 ./scripts/launch_client.sh
```

**Launch arg** (if your Proton/Steam launch passes argv into the game):

```text
-connect=127.0.0.1:27025
```

After the main menu opens, the mod connects once.

Automation boot patches are enabled automatically only when `7DTD_CONNECT` or
`-connect` supplies a launch target. A regular client launch still loads the
`connect` console command and diagnostics, but leaves stock login, menu, EULA,
Discord, authentication, and general loading behavior alone. A narrowly scoped
workaround keeps offline Local-platform world initialization from stalling under
Proton. Stock creates the local player with synchronous addressable loads that
end in `Addressables.WaitForCompletion()`, which deadlocks while any async
addressable operation is still in flight; automation never hits this because it
forces every load sync from boot. The workaround drains `World.LoadWorld`
synchronously, then after `createWorld` waits for `LoadManager`'s async queue to
empty and holds sync loading until `StartAsServer` finishes, so player creation
runs against an idle addressables system. Startup tracing is available with
`diag on`. Specialized runners without a launch target can enable that
automation boot mode explicitly with `7DTD_CONNECT_AUTOMATION=1`.

### Synchronous-load override

Automation boot mode enables the client's global `LoadManager.forceLoadSync`
override by default so addressable loads cannot starve during unattended Proton
launches. To keep all connect features active while using the stock asynchronous
client loading path, set `7DTD_CONNECT_FORCE_LOAD_SYNC=0`. For example, in the
Steam launch options:

```text
env 7DTD_CONNECT_FORCE_LOAD_SYNC=0 mangohud %command%
```

The values `0`, `false`, `no`, and `off` disable only the synchronous-load
override used by automation. Any other value, or leaving the variable unset,
keeps it enabled. Regular client launches do not enable that global override,
so this variable is unnecessary for ordinary Steam play.

### Local player identity for an isolated test client

The stock Local platform derives its identity from `GamePrefs.PlayerName`.
Set `7DTD_PLAYER_NAME` before launch to select that identity before the
auto-join runs.

That identity also names the save's player file, so **switching platform
changes which character a save loads**. A world played under Steam stores
`Saves/<world>/<game>/Player/EOS_<id>.ttp`; the same world opened with
`CLIENT_PLATFORM=local` looks for `Local_<name>.ttp`, does not find it, and
spawns a fresh character in the existing world (`PlayerSpawnedInWorld
(reason: NewGame)` rather than `LoadedGame`). Nothing is lost; the original
`.ttp` stays on disk and comes back under the original platform. But a test
that means to exercise the load-an-existing-character path has to check that
reason, or it silently tests the new-character path instead. This is useful only for an isolated second client in a real
multi-client test: the server sees and authorizes a normal distinct player,
and will reject a duplicate identity. It persists the chosen name in that
client profile, so use a dedicated Proton profile for automation rather than
the player's everyday profile.

The name is normalized before it is stored: control and invisible-format
characters become spaces, and anything past 24 characters is dropped (never
mid-surrogate-pair). The name reaches the server and its logs, so a value
carrying a newline or a bidi override would forge a line there.

Launch a peer named `atomic-peer`:

```bash
env 7DTD_PLAYER_NAME=atomic-peer 7DTD_CONNECT=127.0.0.1:27025 ./scripts/launch_client.sh
```

### Diagnosing in-world frame hitches

A Local host runs a frame-hitch monitor that logs only under `diag on`. Open
the F1 console in-world, arm it, and play for a few minutes:

```text
diag on
```

To start verbose from boot instead of toggling in-world, launch with
`7DTD_CONNECT_DEBUG=1` (`0`/`false`/`no`/`off` keep it off; any other non-empty
value enables it).

Every frame over 200 ms then logs one line with the GC generation deltas, the
`LoadManager` backlog, the managed heap, and the live frame cap:

```text
[7dtd-fastconnect] hitch 412ms frame 9214 gc +3/+1/+0 pendingLoads 0 heap 2841MB targetFps -1 vsync 0
```

`targetFps` and `vsync` are what the renderer is actually running with, not
what the options screen claims. Change Options → Video → FPS Limit In Game and
check whether the next hitch line reports the new value: if it does not, the
cap is not reaching the renderer, which is a different bug from a stutter.
Pair it with `gpu_busy_percent` for whether the GPU is the constraint:

```bash
watch -n1 cat /sys/class/drm/card*/device/gpu_busy_percent
```

`diag off` stops the logging; the coroutine keeps running either way, so diag
can be toggled mid-session without a restart.

### Client audio mute (default on)

`launch_client.sh` **mutes the game process at the OS audio layer by default**
(`pactl` sink-input mute) so automated runs do not blast speakers. This does
**not** change game client settings (no GamePrefs / in-game audio sliders /
registry). Independent of master volume. Requires `pactl` and `jq`.

| Env | Meaning |
|---|---|
| `CLIENT_MUTE` / `SEVEN_DAYS_TO_DIE_CLIENT_MUTE` | Default `1` (muted). Set `0` / `false` / `no` / `off` to leave audio on |
| `CLIENT_MUTE_TIMEOUT` / `SEVEN_DAYS_TO_DIE_CLIENT_MUTE_TIMEOUT` | Seconds to wait for the audio stream after launch, 1..3600 seconds (default 60) |
| `MUTE_POLL_STOP_GRACE_SEC` | Seconds the mute poller gets to exit when the client stops before the launcher kills it (default 5) |
| `CLIENT_PLATFORM=local` | No-Steam client mode: backs up the game's `platform.cfg`, selects the `Local` platform with EOS crossplay off, restores on exit. A second launcher on the same install reuses the running swap instead of taking the backup slot. Lets the real client join a test server without valid Steam auth and without a server-side bypass mod (loadgen bots already ride this path). See `../7dtd-loadgen/docs/STOCK_AUTH.md` |

```bash
# Keep sound for a manual session
CLIENT_MUTE=0 ./scripts/launch_client.sh
```

Run the client on another graphics API:

```bash
GFX_API=vulkan ./scripts/launch_client.sh
```

`d3d11` stays the default because that is what the game ships with on Windows
and through Proton, so every existing run keeps measuring what it measured
before. It is a variable rather than a constant because **Unity takes the first
`-force-*` argument it is given**, so a hardcoded one cannot be overridden by
appending another, which left this launcher unable to drive a client on
OpenGL or Vulkan at all. Anything checking that a shader renders on more than
one graphics API needs exactly that.

WirePlumber may persist mute by `application.name`. Unmute while the client
is running:

```bash
./scripts/unmute_client_audio.sh
```

Clearing the mute needs a live stream so WirePlumber writes the unmuted
state back. With the game closed the script reports whether the saved
state is still muted. It exits 1 when it could not unmute a live stream,
so a silent client is never reported as an unmuted one.

## Environment variables

Every runtime knob in one place; the sections above carry the detail. Rules
that hold for all of them:

- Unset and empty mean the default. Boolean opt-outs accept `0` / `false` /
  `no` / `off` (any case, surrounding whitespace ignored), and opt-ins `1` /
  `true` / `yes` / `on`; any other non-empty value still opts in but logs a
  warning naming the variable and the value, so a typo cannot look like a
  deliberate setting.
- Values echoed to logs are neutralized first, so a terminal cannot be made to
  render a line differently from the text a harness greps for: control
  characters (newline, the C1 block, U+2028/U+2029) become spaces, and the
  invisible Unicode format characters (bidi overrides, LRM/RLM, zero-width
  joiners, BOM) are dropped by `scripts/log_sanitize.sh`, which is why a value
  echoed there is shorter than the one set. The mod's own echoes
  (`Source/ConnectMod/LogText.cs`) blank each of those instead, keeping offsets
  and lengths intact.
- Invalid enum values either abort with the valid set (`GFX_API`) or warn and
  fall back (`CLIENT_MUTE`, `CLIENT_PLATFORM`, numeric timeouts); nothing is
  silently ignored.

| Variable | Default | Controls |
|---|---|---|
| `7DTD_CONNECT` | unset | Auto-join target `host[:port]` once the main menu opens (same as `-connect=host:port`; port defaults to 27025) |
| `7DTD_CONNECT_AUTOMATION` | auto: on when a join target is present | Force automation boot mode on/off explicitly |
| `7DTD_CONNECT_FORCE_LOAD_SYNC` | on in automation mode | `0` keeps stock async loading while connect features stay active |
| `7DTD_CONNECT_DEBUG` | off | Verbose `[7dtd-fastconnect]` traces from boot (same as F1 `diag on`) |
| `7DTD_PLAYER_NAME` | stored `PlayerName` pref | Local-platform identity used for the auto-join |
| `GAME` | stock Steam client path | Client install dir (launcher, harnesses, `make build/install`) |
| `PROTON` / `COMPAT` / `STEAM_ROOT` / `STEAM_APPID` | auto-detected | Proton binary, compatdata prefix, Steam root, app id overrides for the launcher |
| `GFX_API` | `d3d11` | Forced backend: `d3d11`, `d3d12`, `vulkan`, `glcore`, or `none`; an invalid value aborts before launch |
| `CLIENT_MUTE` (+ alias `SEVEN_DAYS_TO_DIE_CLIENT_MUTE`) | `1` | OS-level mute of the game audio stream at launch; an undocumented value warns and mutes |
| `CLIENT_MUTE_TIMEOUT` (+ alias `SEVEN_DAYS_TO_DIE_CLIENT_MUTE_TIMEOUT`) | `60` | Seconds the launcher polls for that stream (1..3600) |
| `MUTE_POLL_STOP_GRACE_SEC` | `5` | Seconds the launcher waits for the mute poller to exit before killing it |
| `CLIENT_PLATFORM` | Steam mode | `1` / `local` / `lan` (case-insensitive) selects no-Steam Local mode; anything else warns and is ignored |

Threat model and known gaps: [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md).

The join harnesses (`one_shot_join.sh`, `zero_nre_join_loop.sh`,
`restart_pair.sh`) take their own knobs (`PORT`, `HOST`, `TIMEOUT_SEC`,
`CYCLE`, `SETTLE_SEC`, `START_SERVER`, `SCRATCH`, plus the zdtd binary path:
`ZDTD_BIN` for the first two, `ZDTD` for `restart_pair.sh`). The numeric and
label knobs are checked at startup and name the fallback they use. `PORT`
must be a real TCP port (1-65535, the same range the client accepts for
`7DTD_CONNECT`); `restart_pair.sh` treats anything else as a usage error, the
other two fall back to 27025. `START_SERVER` reads the boolean table above
(`1` / `true` / `yes` / `on`, `0` / `false` / `no` / `off`), so
`START_SERVER=true` starts the server instead of leaving the cycle to report
"no listener on PORT". `HOST` is not pattern-checked: it is passed to the
client as `7DTD_CONNECT` and the client rejects it there. The zdtd binary is
checked at the point of use, not at startup, so a `START_SERVER=0` loop runs
without the server checked out.

Every lifecycle entry point in `scripts/` (`launch_client.sh`, the three join
harnesses, `mute_client_audio.sh`, `unmute_client_audio.sh`, `repro_zip.sh`,
`package.sh`, `stage_mod.sh`, `assert_tool_pin.sh`; `scripts/test_cli_help.sh`
enumerates them) follows one contract: `-h` / `--help` prints the usage on
stdout and exits 0 before touching disk or spawning a process, an argument a
script cannot accept exits 2 with the usage on stderr, and a setup or runtime
failure exits 1. The harnesses add their own statuses on top
(`one_shot_join.sh` also uses 3 for a server that never listened); `--help`
lists them. The sourced libraries (`log_sanitize.sh`, `proton_paths.sh`, the
`test_*.sh` gates) take no arguments and are not part of that contract.

## With zdtd

```bash
# terminal 1
cd zdtd-server && ./zig-out/bin/zdtd --port 27025 ...

# terminal 2
cd 7dtd-fastconnect && make install
env 7DTD_CONNECT=127.0.0.1:27025 ./scripts/launch_client.sh
```

Or with client already running: F1 → `connect 127.0.0.1 27025`.

## Log lines

```text
[7dtd-fastconnect] InitMod ...
[7dtd-fastconnect] player name applied from 7DTD_PLAYER_NAME
[7dtd-fastconnect] auto-join from 7DTD_CONNECT=127.0.0.1:27025
[7dtd-fastconnect] Connect by IP 127.0.0.1:27025 ...
```

## Non-goals

- Steam `steam://connect` (does not work for non-Steam servers)
- Server-side code
- EAC-on
