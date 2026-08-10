# zdtd-connect (client mod)

Tiny **client** helper for joining local/dev servers (especially **zdtd**) without Steam `steam://connect`, plus automation hooks for automated join tests.

Steam connect fails for zdtd (`app id specified by server is invalid`) because zdtd is not a Steam Game Server. This mod calls the same path as **Connect to IP**.

**Scope (v0.9+):** connect / auto-join / skip news+Discord for headless testing only.
Missing terrain, signs, inventory, spawn, or deco behaviour is fixed on the **server**
(zdtd), never by inventing world state in this mod.

**Gameplay automation** (dig/place/suites, scored exit codes) lives in sibling
[`../7dtd-playtest/`](../7dtd-playtest/). Install both mods for automated playtests.

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

Installs to `$GAME/Mods/zdtd-connect/`.

## Usage

### F1 console (main menu)

```text
connect 127.0.0.1
connect 127.0.0.1 27025
connect 127.0.0.1:27025
```

Aliases: `zdtdconnect`, `joinip`. Default port **27025** (zdtd ServerPort / Connect-to-IP port).

### Auto-join on main menu

**Environment (preferred):**

```bash
export ZDTD_CONNECT=127.0.0.1:27025
# or: 7DTD_CONNECT=127.0.0.1:27025
```

**Launch arg** (if your Proton/Steam launch passes argv into the game):

```text
-connect=127.0.0.1:27025
```

Helper:

```bash
ZDTD_CONNECT=127.0.0.1:27025 ./scripts/launch_client.sh
```

After the main menu opens, the mod connects once.

### Client audio mute (default on)

`launch_client.sh` **mutes the game process at the OS audio layer by default**
(`pactl` sink-input mute) so automated runs do not blast speakers. This does
**not** change game client settings (no GamePrefs / in-game audio sliders /
registry). Independent of master volume. Requires `pactl` and `jq`.

| Env | Meaning |
|---|---|
| `CLIENT_MUTE` / `SEVEN_DAYS_TO_DIE_CLIENT_MUTE` | Default `1` (muted). Set `0` / `false` / `no` / `off` to leave audio on |
| `CLIENT_MUTE_TIMEOUT` | Seconds to wait for the audio stream after launch (default 60) |

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
ZDTD_CONNECT=127.0.0.1:27025 ./scripts/launch_client.sh
```

Or with client already running: F1 → `connect 127.0.0.1 27025`.

## Log lines

```text
[zdtd-connect] InitMod ...
[zdtd-connect] auto-join from ZDTD_CONNECT=127.0.0.1:27025
[zdtd-connect] Connect by IP 127.0.0.1:27025 ...
```

## Non-goals

- Steam `steam://connect` (does not work for non-Steam servers)
- Server-side code
- EAC-on
