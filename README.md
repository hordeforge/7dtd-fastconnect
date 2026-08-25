# ⚡ 7DTD FastConnect

> **Part of [HordeForge](https://github.com/hordeforge)** — High-Performance Systems Engineering for 7 Days to Die.

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

## Requirements

- Stock client **EAC off** (`-noeac`; C# mods require it)
- `0_TFP_Harmony` present (stock)
- Game at `~/.local/share/Steam/steamapps/common/7 Days To Die` (override with `GAME=`)

## Skips (faster boot)

| Screen | How |
|---|---|
| TFP intro splash video | Process arg **`-skipintro`** (must be on argv; splash runs before mods) |
| News “click to continue” | **`-SkipNewsScreen=true`** + Harmony forces `shownNewsScreenOnce` / blocks `XUiC_NewsScreen.Open` |
| EULA accept gate | Harmony forces `HasAcceptedLatestEula=true` and blocks the `windowEula` window: accepts latest and reopens the main menu |
| Opener movie on world load | `showOpenerMovieOnLoad = false`, `OptionsIntroMovieEnabled = false` |
| Discord login / SDK | `GamePrefs.DiscordDisabled=true` + Harmony skips `DiscordManager.Init` and Discord first-time menu |

`scripts/launch_client.sh` always adds `-skipintro -SkipNewsScreen=true`.

## Install

```bash
cd 7dtd-fastconnect
make install
```

Installs to `$GAME/Mods/7dtd-fastconnect/`.

## Tests

```bash
make test
```

The whole suite is offline: no game install, no server, no audio daemon.
`test_connect_target_parse.sh` is behavioral rather than structural — it
compiles the real `Source/ConnectMod/ConnectTarget.cs` with `mcs` against
compiler-only game-API stubs (`scripts/testdata/`) and runs target parsing and
launch-context resolution for real. It skips itself when mono is absent. The
shellcheck / ruff / mypy / pytest gates run last and warn when a tool is
missing.

## Usage

### F1 console (main menu)

```text
connect 127.0.0.1
connect 127.0.0.1 27025
connect 127.0.0.1:27025
```

Aliases: `7dtdconnect`, `joinip`. Default port **27025** (zdtd ServerPort / Connect-to-IP port).

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
spawns a fresh character in the existing world — `PlayerSpawnedInWorld
(reason: NewGame)` rather than `LoadedGame`. Nothing is lost; the original
`.ttp` stays on disk and comes back under the original platform. But a test
that means to exercise the load-an-existing-character path has to check that
reason, or it silently tests the new-character path instead. This is useful only for an isolated second client in a real
multi-client test: the server sees and authorizes a normal distinct player,
and will reject a duplicate identity. It persists the chosen name in that
client profile, so use a dedicated Proton profile for automation rather than
the player's everyday profile.

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
| `CLIENT_MUTE_TIMEOUT` | Seconds to wait for the audio stream after launch (default 60) |
| `CLIENT_PLATFORM=local` | No-Steam client mode: backs up the game's `platform.cfg`, selects the `Local` platform with EOS crossplay off, restores on exit. Lets the real client join a test server without valid Steam auth and without a server-side bypass mod (loadgen bots already ride this path). See `../7dtd-loadgen/docs/STOCK_AUTH.md` |

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
appending another — which left this launcher unable to drive a client on
OpenGL or Vulkan at all. Anything checking that a shader renders on more than
one graphics API needs exactly that.

WirePlumber may persist mute by `application.name`. Unmute while the client
is running:

```bash
./scripts/unmute_client_audio.sh
```

Clearing the mute needs a live stream so WirePlumber writes the unmuted
state back. With the game closed the script reports whether the saved
state is still muted.

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
[7dtd-fastconnect] player name from 7DTD_PLAYER_NAME=atomic-peer
[7dtd-fastconnect] auto-join from 7DTD_CONNECT=127.0.0.1:27025
[7dtd-fastconnect] Connect by IP 127.0.0.1:27025 ...
```

## Non-goals

- Steam `steam://connect` (does not work for non-Steam servers)
- Server-side code
- EAC-on
