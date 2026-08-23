# AGENTS.md - 7dtd-fastconnect

Client-only mod: join by IP without Steam `steam://connect` (invalid for non-Steam servers like **zdtd-server**).

Canonical modding guide: [MODDING_BEST_PRACTICES.md](https://github.com/hordeforge/.github/blob/main/MODDING_BEST_PRACTICES.md)

## Owns

- `Mods/7dtd-fastconnect` under the **stock client** game dir
- F1 `connect` / env `7DTD_CONNECT` / `-connect=host:port` auto-join after main menu
- Intro/news/Discord/EULA skip and spawn-gate heartbeat for automated launch
- Lifecycle scripts (`launch_client.sh`, `restart_pair.sh`, `one_shot_join.sh`)

## Does not own

- Server (zdtd / stock dedi)
- Steam protocol, EAC-on
- **Gameplay scenarios** (move/dig/craft/assert suites): that is
  [`../7dtd-playtest/`](../7dtd-playtest/)

## Rules

1. **EAC off** required (any C# client mod).
2. Port is **Connect-to-IP / ServerPort** (e.g. 27025), not LiteNet +2.
3. Keep the mod tiny: **join/automation plumbing only** (F1 connect, env/CLI
   auto-join, optional intro/news skip that blocks headless/automated launch,
   lifecycle scripts). **No gameplay and no server-gap workarounds.**
4. **Do not implement missing zdtd (or stock server) features in this client.**
   Forbidden: local chunk/terrain/deco generation, client-side sign library
   synthesis, faking world-init/spawn/inventory packages, force-setting flags
   the server should send, NRE/error swallow, or Harmony that replaces absent
   S2C with invented client state. If join looks void or spammy, fix **zdtd**
   (or the real dedi), not this mod. Workspace rule: root `AGENTS.md` #10.
5. **No server-gap patches.** Former LocalChunk/sign/spawn/NRE workarounds were
   removed in v0.9.0. Missing terrain/join behaviour is fixed in **zdtd**, not
   here.
6. **No playtest driver here.** Scripted dig/place/combat lives in
   `7dtd-playtest`. `restart_pair.sh` may still pass `PLAYTEST*` env through
   to the client for the playtest mod.

## Commands

```bash
make test     # offline gates (env naming, mute helper, platform swap/restore)
make build    # requires local client install (game Assembly-CSharp)
make package  # build + zip dist/7dtd-fastconnect-<tag>.zip (needs a client install)
make install
env 7DTD_CONNECT=127.0.0.1:27025 ./scripts/launch_client.sh
```

## Stock-game research -> 7dtd-engine-research

Anything that studies the **stock** dedicated server belongs in
[`../7dtd-engine-research/`](../7dtd-engine-research/), not here: reverse-engineering
narratives (`docs/`), the Mono.Cecil dump tooling (`tools/`), wire/protocol
analysis, and engine cost/loop RE. This repo owns the client-only direct-connect mod;
it does not host stock-game RE docs or dumpers. When RE is needed, add it
under `../7dtd-engine-research/` and link back. How to RE:
[`../7dtd-engine-research/docs/re-methodology.md`](../7dtd-engine-research/docs/re-methodology.md).
