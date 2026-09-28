# Personal data handled by this mod

What the mod reads, where it writes it, who else sees it, and how to change or
clear it. Every claim here names the code that implements it; the data flow
side (attackers, forged markers) is in [THREAT_MODEL.md](THREAT_MODEL.md).

The mod is a client-side join helper. It ships no analytics, no telemetry, no
third-party SDK and no HTTP client: the only network traffic it initiates is
the game connecting to the target server. It holds no server, no account
database and no credentials of its own; Steam and EOS credentials stay inside
the stock client.

## Inventory

| Field | Where it comes from | Where it is written | Who else sees it |
|---|---|---|---|
| Player display name | env `7DTD_PLAYER_NAME`, or in automation mode the OS user name, then the machine name, then `player` (`Source/ConnectMod/PlayerNames.cs:54`) | the stock `EnumGamePrefs.PlayerName` pref in the client profile, via `GamePrefs.Set` and `Save()` (`Source/ConnectMod/ModApi.cs:196`) | the server it joins: the name travels in the login and lands in that server's logs and player list, which this repo does not control |
| Synthetic platform id | FNV-1a hash of the machine name, else the OS user name, else a fixed value (`Source/ConnectMod/AuthFallbackPatches.cs:74`) | not stored by the mod; sent as the platform user id at join | the same server, which persists player data against it |
| Harness artifacts | the client's own log file | `SCRATCH` (`~/.cache/7dtd-fastconnect` by default), pruned by age and count (`scripts/one_shot_join.sh:59`) | nobody; the files stay on the machine that ran the harness |

The auth ticket is not on this list: with no Steam or EOS login the mod
substitutes an **empty** ticket rather than a stored one
(`Source/ConnectMod/AuthFallbackPatches.cs:11`, `:161`).

## The display name

- **Set it**: `7DTD_PLAYER_NAME=<name>` before launch. The value is normalized
  before it is stored: control, line-breaking and invisible-format characters
  become spaces, the result is trimmed, NFC-normalized and cut to 24 code
  points. A value that normalizes away falls back to the resolved name rather
  than storing an empty pref.
- **Leave it alone**: outside automation mode, a client that already has a
  stored name and no `7DTD_PLAYER_NAME` writes nothing
  (`Source/ConnectMod/ModApi.cs:163`).
- **In automation mode** (`7DTD_CONNECT` or `-connect=` present), an empty
  stored pref is filled in from the OS user name so a Steam-less loopback join
  is not kicked for an empty name.
- **Never in the client log**: the value itself is not logged. Only its source
  is, as one of two fixed lines (`Source/ConnectMod/ModApi.cs:205`). The client
  log is the artifact people paste into issue reports, so the name stays out of
  it; `scripts/test_player_name_override.sh` pins that invariant.

To change or clear the stored name, edit it in the stock main menu (the same
field the game shows) or remove the `PlayerName` entry from the client profile.
Unsetting `7DTD_PLAYER_NAME` stops the mod from writing it again, but does not
revert a name it already stored; that is stock preference state, and the mod
only overwrites it when the stored value is empty or the env asks for a
different one.

## The synthetic platform id

Only the Steam-less path uses it, and only when no Steam login is present, so
the stock dedi's empty-player-id check does not kick a LAN join. A real Steam
login always wins and the real id is sent (`Source/ConnectMod/AuthFallbackPatches.cs:103`).

It is a hash, not an encryption: the seed is a host or account name, so an id
that appears in a shared server log can be matched against guessed host names
by brute force. That is accepted for a synthetic id that exists only to
satisfy a name check, and it is why the id is never logged locally. The
alternative (a random per-installation id) would need a secret persisted
outside the game profile, which costs a file this mod does not otherwise
write; a public constant salt would not help against the same attack.

## Artifacts and retention

The harness scripts keep their artifacts under `SCRATCH`: the client log copy,
the launcher output, the per-cycle control log and the server log. They are
pruned to the 20 newest of each kind and dropped after three days
(`scripts/one_shot_join.sh:59`, `scripts/zero_nre_join_loop.sh:56`). A control
log also holds up to 80 lines copied from the client log
(`scripts/join_evidence.sh:35`), and the server-log tail the harnesses print
when the server never listened.

Those copied lines pass through `redact_personal_log_text`
(`scripts/join_evidence.sh`), which replaces the values the stock log prints
for a person with `<redacted>`, keeping the label so the line still reads as
evidence:

| Copied line | Redacted | Kept |
|---|---|---|
| `PlayerLogin: <name>` | the display name | the label |
| `Client IP: <addr>` | the peer address | the label |
| `PlayerId(<entity>, <id>)` | the platform user id | the entity id |
| `Player '<name>' died` / `killed by '<name>'` | both names | the verb |
| `Player <name> disconnected after N minutes` | the name | the verb |

The formats are the stock ones, read out of the shipped `Assembly-CSharp.dll`.
The redaction covers the control log and the printed server tail, not the
copies of the raw logs, which stay on the machine that ran the harness. Clear a
scratch directory by deleting it; nothing outside it is written by these scripts
except the stock `platform.cfg` swap that `launch_client.sh` restores on exit.

## Rights a user has here

This is a local mod, so access, rectification and erasure are file operations
on the machine that runs it, not requests to an operator:

- **Access**: the stored name is a pref in the stock client profile.
- **Rectification**: `7DTD_PLAYER_NAME`, or the stock main-menu field.
- **Erasure**: clear the pref, delete the scratch directory, delete the client
  log. The control log and the printed server tail hold redacted copies only, so
  deleting the raw logs removes the values; server-side records (player list,
  saved player data) belong to the server operator and are outside this repo.
