# 7dtd-connect (client mod)

Tiny **client** helper for joining local/dev servers (especially **zdtd**) without Steam `steam://connect`, plus automation hooks for automated join tests.

Steam connect fails for zdtd (`app id specified by server is invalid`) because zdtd is not a Steam Game Server. This mod calls the same path as **Connect to IP**.

**Scope (v0.9+):** connect / auto-join / skip news+Discord for headless testing only.
Missing terrain, signs, inventory, spawn, or deco behaviour is fixed on the **server**
(zdtd), never by inventing world state in this mod.

**Gameplay automation** (dig/place/suites, scored exit codes) lives in sibling
[`../7dtd-playtest/`](../7dtd-playtest/). Install both mods for automated playtests.

CI runs `make test` on every push and PR. Packaged builds are attached to
GitHub releases (`make package` produces `dist/7dtd-connect-<tag>.zip`).

## Requirements

- Stock client **EAC off** (`-noeac`; C# mods require it)
- `0_TFP_Harmony` present (stock)
- Game at `~/.local/share/Steam/steamapps/common/7 Days To Die` (override with `GAME=`)

## Skips (faster boot)

| Screen | How |
|---|---|
| TFP intro splash video | Process arg **`-skipintro`** (must be on argv; splash runs before mods) |
| News “click to continue” | **`-SkipNewsScreen=true`** + Harmony forces `shownNewsScreenOnce` / blocks `XUiC_NewsScreen.Open` |
| Opener movie on world load | `showOpenerMovieOnLoad = false`, `OptionsIntroMovieEnabled = false` |
| Discord login / SDK | `GamePrefs.DiscordDisabled=true` + Harmony skips `DiscordManager.Init` and Discord first-time menu |

`scripts/launch_client.sh` always adds `-skipintro -SkipNewsScreen=true`.

## Install

```bash
cd 7dtd-connect
make install
```

Installs to `$GAME/Mods/7dtd-connect/`.

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
Discord, authentication, and frame/loading behavior alone. Specialized runners
without a launch target can force the old behavior with
`7DTD_CONNECT_AUTOMATION=1`.

### Synchronous-load override

Automated playtests enable the client's global `LoadManager.forceLoadSync`
override by default so addressable loads cannot starve during unattended Proton
launches. To keep all connect features active while using the stock asynchronous
client loading path, set `7DTD_CONNECT_FORCE_LOAD_SYNC=0`. For example, in the
Steam launch options:

```text
env 7DTD_CONNECT_FORCE_LOAD_SYNC=0 mangohud %command%
```

The values `0`, `false`, `no`, and `off` disable only the synchronous-load
override. With the variable unset, playtest behavior is unchanged.

### Local player identity for an isolated test client

The stock Local platform derives its identity from `GamePrefs.PlayerName`.
Set `7DTD_PLAYER_NAME` before launch to select that identity before the
auto-join runs. This is useful only for an isolated second client in a real
multi-client test: the server sees and authorizes a normal distinct player,
and will reject a duplicate identity. It persists the chosen name in that
client profile, so use a dedicated Proton profile for automation rather than
the player's everyday profile.

Launch a peer named `atomic-peer`:

```bash
env 7DTD_PLAYER_NAME=atomic-peer 7DTD_CONNECT=127.0.0.1:27025 ./scripts/launch_client.sh
```

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

WirePlumber may persist mute by `application.name`. Unmute while the client
is running (desktop volume UI, or `pactl set-sink-input-mute <index> 0`).

## With zdtd

```bash
# terminal 1
cd zdtd && ./zig-out/bin/zdtd --port 27025 ...

# terminal 2
cd 7dtd-connect && make install
env 7DTD_CONNECT=127.0.0.1:27025 ./scripts/launch_client.sh
```

Or with client already running: F1 → `connect 127.0.0.1 27025`.

## Log lines

```text
[7dtd-connect] InitMod ...
[7dtd-connect] player name from 7DTD_PLAYER_NAME=atomic-peer
[7dtd-connect] auto-join from 7DTD_CONNECT=127.0.0.1:27025
[7dtd-connect] Connect by IP 127.0.0.1:27025 ...
```

## Non-goals

- Steam `steam://connect` (does not work for non-Steam servers)
- Server-side code
- EAC-on
