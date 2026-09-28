# Security Policy

## What this project is, in security terms

`7dtd-fastconnect` is a **client-side mod**. It adds no server, no
listener, no update mechanism, and no network endpoint of its own. Its
security-relevant surface is:

- the files it reads and writes on the machine it runs on (the game's own
  `GamePrefs` store, and `platform.cfg` when a launcher swaps it),
- the outbound connection it makes to whatever host the operator names,
- the log lines the mod prints, which the test harnesses treat as evidence.

The full model of that surface, with a file reference for every entry point,
boundary, threat and mitigation, is [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md).

## Supported versions

The current release line is **0.12.x**. The version is declared three times
(`ModInfo.xml`, `Source/ConnectMod/ModApi.cs`, `pyproject.toml`) and
`scripts/test_version_sync.sh`, run by the release workflow, refuses a tag
that disagrees with them.

Older tags (0.11.0 and earlier) are kept for reproduction. Nothing in this
repository backports to them: there is no maintenance branch, and the CI and
release workflows only ever run against the tag that was just pushed. **A
security fix therefore lands on the current line only.** Whether the older
lines should be maintained is an open decision for the maintainers; this
document does not claim a policy either way.

## Preconditions a user must know

- **EAC must be off** for any C# client mod, this one included. Running
  with EAC on is unsupported, not merely discouraged.
- This mod joins a server **by IP**, bypassing the Steam friends/join flow.
  The destination host is whatever the operator supplies through the F1
  console, `7DTD_CONNECT`, or `-connect=`. Under automated launch the
  client presents a synthetic, machine-derived identity with an empty
  authentication ticket when no Steam/EOS login exists. That identity is
  predictable from the machine name. **Treat automated, Steam-less clients
  as loopback/LAN test clients only**, and rely on server-side
  authorization for anything else. See R1 and R2 in the threat model.
- The mod deliberately does not implement missing server behavior. If
  join looks wrong, the fix belongs in the server, not here.

## Release artifacts

Release zips are built on a maintainer's machine and attached to the
GitHub release by hand; CI does not build or sign them. Each zip ships
beside a `.buildinfo` record naming the version, commit, dirty flag,
`SOURCE_DATE_EPOCH`, dotnet version and the archive's SHA-256, and the
archive is verified against the staged payload before it is written. That
record is produced by the same machine that produced the payload, so it
is a reproducibility aid, not an authenticity proof: **check the digest
against a source you trust, not against the `.buildinfo` next to the
file.**

## Reporting a vulnerability

**No reporting contact is defined for this repository yet.** The project
has no published security policy address, no private advisory channel and
no designated security owner, and this document does not invent one.

Until that is decided, a report is best filed as a GitHub issue on the
repository, describing the version, the entry point involved and the
observed behavior. **Do not open a public issue for a vulnerability that
has not been fixed yet**, especially for anything touching the launch
context, the identity/auth path or a release artifact.

This gap is recorded as gap 7 in [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md).
