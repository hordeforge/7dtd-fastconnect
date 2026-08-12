#!/usr/bin/env python3
"""Offline gate for the CLIENT_PLATFORM=local swap/restore plumbing.

Runs launch_client.sh against a fake game dir + stub Proton: the game's
platform.cfg must be swapped to Local before launch and restored afterwards
(the trap), without touching the real install.
"""

from __future__ import annotations

import os
import stat
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LAUNCH = ROOT / "scripts" / "launch_client.sh"
STEAM_CFG = "platform=Steam\ncrossplatform=EOS\nserverplatforms=Steam,XBL,PSN,LAN,\n"
LOCAL_CFG = "platform=Local\ncrossplatform=None\nserverplatforms=Steam,LAN,Local,\n"


def _run(tmp_path: Path) -> subprocess.CompletedProcess[str]:
    game = tmp_path / "game"
    game.mkdir()
    (game / "platform.cfg").write_text(STEAM_CFG, encoding="utf-8")
    exe = game / "7DaysToDie.exe"
    exe.write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8")
    exe.chmod(exe.stat().st_mode | stat.S_IEXEC)
    proton = tmp_path / "proton-stub"
    proton.write_text("#!/usr/bin/env bash\nshift\nexec \"$@\"\n", encoding="utf-8")
    proton.chmod(proton.stat().st_mode | stat.S_IEXEC)
    compat = tmp_path / "compat"
    env = {
        **os.environ,
        "GAME": str(game),
        "PROTON": str(proton),
        "COMPAT": str(compat),
        "CLIENT_PLATFORM": "local",
        "CLIENT_MUTE": "0",
        "STEAM_COMPAT_CLIENT_INSTALL_PATH": str(tmp_path / "steam"),
        "HOME": str(tmp_path / "home"),
    }
    (tmp_path / "home").mkdir()
    return subprocess.run(
        ["bash", str(LAUNCH)], env=env, capture_output=True, text=True, timeout=60,
    )


def test_local_platform_swap_and_restore(tmp_path):
    game = tmp_path / "game"
    r = _run(tmp_path)
    assert r.returncode == 0, r.stderr
    # Trap restored the original config.
    assert (game / "platform.cfg").read_text(encoding="utf-8") == STEAM_CFG
    # No leftover backup.
    assert not (game / "platform.cfg.re-localbak").exists()


def test_no_platform_override_leaves_config_alone(tmp_path):
    game = tmp_path / "game"
    game.mkdir()
    (game / "platform.cfg").write_text(STEAM_CFG, encoding="utf-8")
    exe = game / "7DaysToDie.exe"
    exe.write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8")
    exe.chmod(exe.stat().st_mode | stat.S_IEXEC)
    proton = tmp_path / "proton-stub"
    proton.write_text("#!/usr/bin/env bash\nshift\nexec \"$@\"\n", encoding="utf-8")
    proton.chmod(proton.stat().st_mode | stat.S_IEXEC)
    (tmp_path / "h").mkdir()
    (tmp_path / "c").mkdir()
    r = subprocess.run(
        ["bash", str(LAUNCH)],
        env={**os.environ, "GAME": str(game), "PROTON": str(proton),
             "COMPAT": str(tmp_path / "c"), "CLIENT_MUTE": "0",
             "HOME": str(tmp_path / "h")},
        capture_output=True, text=True, timeout=60,
    )
    assert r.returncode == 0, r.stderr
    assert (game / "platform.cfg").read_text(encoding="utf-8") == STEAM_CFG
