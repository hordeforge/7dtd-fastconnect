# Contributing

Client-only 7 Days To Die mod. Scope and the "no server-gap workarounds" rule
live in [AGENTS.md](AGENTS.md); the user-facing docs are [README.md](README.md).
This file is the runnable path: what a change has to pass before it is pushed.

## Setup

```bash
make doctor                     # names every tool this machine is missing
uv sync --locked --group dev    # the pinned ruff/mypy/pytest/yamllint
```

`make doctor` is the whole toolchain check. Running the tests needs no game
install, no server, and no network beyond the first `uv sync`. The dotnet SDK
band is pinned in `global.json`, the Python tools in `pyproject.toml` /
`uv.lock`, and the NuGet graph in `Source/ConnectMod/packages.lock.json`;
`make test` fails rather than running an unpinned tool.

`make build`, `make install`, and `make package` are the only targets that
compile against a game install. They need one; `make check-game-root` names
what is missing before the build starts.

## The edit-test loop

```bash
make test     # every gate CI runs: the full local verification
make gate GATE=scripts/test_log_sanitize.sh              # one shell gate
make gate GATE=scripts/test_launch_client_platform.py    # one Python gate
make gate GATE=scripts/test_launch_client_platform.py GATE_ARGS=-kresolve_compat
```

A `.sh` gate is a program that prints `PASS`/`FAIL` lines and a final
`RESULT PASS`; a `.py` gate is a pytest module and goes through pytest, never
straight to the interpreter. `make gate` with no `GATE` prints its usage and
exits 2.

The conventions when adding to either kind:

- A shell gate lives in `scripts/test_<subject>.sh`, sources
  `scripts/test_common.sh`, and calls `assert` / `assert_fails` / `finish`.
  Its working dir comes from `scratch_mktemp "$ROOT"`, never `mktemp -d` bare.
- A Python gate lives in `scripts/test_<subject>.py` and uses `tmp_path`.
- A new gate goes into `GATES` in the `Makefile`. An ungated script is a
  script CI never runs.

## Before you push

- `make test` is green. CI runs that exact target on every push and PR, so
  anything else you verify locally does not stand in for it.
- The shell sources pass `shellcheck -S warning`, the Python passes
  `ruff check`, `ruff format --check`, and `mypy --strict`; all four run as
  part of `make test`, with the versions pinned above.
- If your change moves a version, it moves it in every declaration at once:
  `ModInfo.xml`, `Source/ConnectMod/ModApi.cs`, and `pyproject.toml`.
  `scripts/test_version_sync.sh` enforces that.
- A change that needs a new NuGet package, or a new Python dev tool, updates
  the matching lockfile through its own tool (`dotnet restore
  Source/ConnectMod/ConnectMod.csproj -p:RestoreLockedMode=false -p:RestorePackagesWithLockFile=true`,
  `uv lock`). Do not hand-edit either lockfile.
- Add a line to the unreleased section of [CHANGELOG.md](CHANGELOG.md) for
  anything a user would notice. The release gate
  (`scripts/changelog_gate.sh`) checks the tagged section has entries; a tag
  that does not match `ModInfo.xml` is rejected by the release workflow.

## Releasing

Maintainers only: [docs/RELEASING.md](docs/RELEASING.md). A `vX.Y.Z` tag must
agree with the version `ModInfo.xml` ships, and the packaged zip is built on a
machine that has the game, not in CI.
