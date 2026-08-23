#!/usr/bin/env python3
"""Offline gate for the CLIENT_PLATFORM=local swap/restore plumbing.

Runs launch_client.sh against a fake game dir + stub Proton: the game's
platform.cfg must be swapped to Local before launch and restored afterwards
(the trap), without touching the real install. Also gates -connect= arg
forwarding from the 7DTD_CONNECT env var (the mod's core join path).

The stub Proton execs its args into a stub game exe that records its argv,
so tests assert what actually reaches the game process.
"""

from __future__ import annotations

import os
import shlex
import stat
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LAUNCH = ROOT / "scripts" / "launch_client.sh"
STEAM_CFG = "platform=Steam\ncrossplatform=EOS\nserverplatforms=Steam,XBL,PSN,LAN,\n"
LOCAL_CFG = "platform=Local\ncrossplatform=None\nserverplatforms=Steam,LAN,Local,\n"

# Vars launch_client.sh reads; scrub them so a developer shell that happens to
# carry 7DTD_CONNECT / CLIENT_* cannot change what these tests exercise.
SCRUB = {
    "GAME", "PROTON", "COMPAT", "STEAM_APPID", "STEAM_ROOT",
    "7DTD_CONNECT", "CLIENT_MUTE", "SEVEN_DAYS_TO_DIE_CLIENT_MUTE",
    "CLIENT_MUTE_TIMEOUT", "SEVEN_DAYS_TO_DIE_CLIENT_MUTE_TIMEOUT",
    "CLIENT_PLATFORM", "STEAM_COMPAT_CLIENT_INSTALL_PATH",
}
BASE_ENV = {k: v for k, v in os.environ.items() if k not in SCRUB}


def _setup(tmp_path: Path) -> Path:
    """Fake install: game dir + platform.cfg + exe stub recording its argv."""
    game = tmp_path / "game"
    game.mkdir(exist_ok=True)
    (game / "platform.cfg").write_text(STEAM_CFG, encoding="utf-8")
    record = tmp_path / "game-argv.txt"
    # Proton runs the exe with cwd=game and passes extra args through.
    exe = game / "7DaysToDie.exe"
    exe.write_text(
        f"#!/usr/bin/env bash\nprintf '%s\\n' \"$@\" > {shlex.quote(str(record))}\n",
        encoding="utf-8",
    )
    exe.chmod(exe.stat().st_mode | stat.S_IEXEC)
    proton = tmp_path / "proton-stub"
    proton.write_text("#!/usr/bin/env bash\nshift\nexec \"$@\"\n", encoding="utf-8")
    proton.chmod(proton.stat().st_mode | stat.S_IEXEC)
    return game


def _launch(
    tmp_path: Path,
    *,
    local_platform: bool = True,
    connect: str | None = None,
) -> subprocess.CompletedProcess[str]:
    env = {
        **BASE_ENV,
        "GAME": str(tmp_path / "game"),
        "PROTON": str(tmp_path / "proton-stub"),
        "COMPAT": str(tmp_path / "compat"),
        "CLIENT_MUTE": "0",
        "HOME": str(tmp_path / "home"),
    }
    if local_platform:
        env["CLIENT_PLATFORM"] = "local"
    if connect is not None:
        env["7DTD_CONNECT"] = connect
    return subprocess.run(
        ["bash", str(LAUNCH)], env=env, capture_output=True, text=True,
        timeout=60, check=False,
    )


def _argv(tmp_path: Path) -> list[str]:
    record = tmp_path / "game-argv.txt"
    assert record.exists(), "stub game exe was never invoked"
    return record.read_text(encoding="utf-8").splitlines()


def test_local_platform_swap_and_restore(tmp_path: Path) -> None:
    game = _setup(tmp_path)
    r = _launch(tmp_path)
    assert r.returncode == 0, r.stderr
    # Trap restored the original config.
    assert (game / "platform.cfg").read_text(encoding="utf-8") == STEAM_CFG
    # No leftover backup.
    assert not (game / "platform.cfg.re-localbak").exists()


def test_no_platform_override_leaves_config_alone(tmp_path: Path) -> None:
    game = _setup(tmp_path)
    r = _launch(tmp_path, local_platform=False)
    assert r.returncode == 0, r.stderr
    assert (game / "platform.cfg").read_text(encoding="utf-8") == STEAM_CFG


def test_leftover_backup_is_restored_then_reswapped(tmp_path: Path) -> None:
    """A hard-killed previous run leaves the cfg swapped + a backup; the next
    launch must restore it first, then swap fresh (self-healing)."""
    game = _setup(tmp_path)
    # Simulate the interrupted state: cfg swapped to Local + backup of Steam.
    (game / "platform.cfg").write_text(LOCAL_CFG, encoding="utf-8")
    (game / "platform.cfg.re-localbak").write_text(STEAM_CFG, encoding="utf-8")
    r = _launch(tmp_path)
    assert r.returncode == 0, r.stderr
    assert "restored from a previous interrupted run" in r.stdout
    # After the clean exit the original Steam config is back.
    assert (game / "platform.cfg").read_text(encoding="utf-8") == STEAM_CFG


def test_connect_env_forwards_connect_arg(tmp_path: Path) -> None:
    _setup(tmp_path)
    r = _launch(tmp_path, connect="127.0.0.1:27025")
    assert r.returncode == 0, r.stderr
    argv = _argv(tmp_path)
    assert "-connect=127.0.0.1:27025" in argv
    assert "-skipintro" in argv


def test_no_connect_env_omits_connect_arg(tmp_path: Path) -> None:
    _setup(tmp_path)
    r = _launch(tmp_path)
    assert r.returncode == 0, r.stderr
    assert not any(a.startswith("-connect=") for a in _argv(tmp_path))
