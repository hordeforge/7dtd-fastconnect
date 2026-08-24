#!/usr/bin/env python3
"""Offline gate for launch_client.sh plumbing.

Runs the launcher against a fake game dir + stub Proton/steam/pactl so each
gate exercises real script behavior without a game install:

- CLIENT_PLATFORM=local swap/restore: platform.cfg must be swapped to Local
  before launch and restored afterwards (the trap), without touching the real
  install.
- 7DTD_CONNECT forwarding: -connect= must reach the game process argv (the
  mod's core join path), and the EAC-off/render flags are part of the
  launcher's contract with the game (any C# client mod requires EAC off).
- COMPAT derivation: a GAME outside the default Steam root must derive its
  Proton prefix from GAME's own library; losing that silently falls through
  to the steam -applaunch branch, which loses environment passthrough.
- Steam fallback: no usable Proton still forwards -connect= and exports
  7DTD_CONNECT to the steam process.
- Client mute: default-on mutes matching sink inputs through the launched
  helper (stub pactl); CLIENT_MUTE=0 never invokes pactl.

The stub Proton execs its args into a stub game exe that records its argv,
so tests assert what actually reaches the game process.
"""

from __future__ import annotations

import os
import shlex
import shutil
import signal
import stat
import subprocess
import time
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
LAUNCH = ROOT / "scripts" / "launch_client.sh"
STEAM_CFG = "platform=Steam\ncrossplatform=EOS\nserverplatforms=Steam,XBL,PSN,LAN,\n"
LOCAL_CFG = "platform=Local\ncrossplatform=None\nserverplatforms=Steam,LAN,Local,\n"
# The launcher forwards SIGTERM to `exit 143`, so the trap-driven cleanup path
# is observable as exactly this status.
TERM_EXIT_STATUS = 128 + int(signal.SIGTERM)

# Vars launch_client.sh reads; scrub them so a developer shell that happens to
# carry 7DTD_CONNECT / CLIENT_* cannot change what these tests exercise.
SCRUB = {
    "GAME", "PROTON", "COMPAT", "STEAM_APPID", "STEAM_ROOT",
    "7DTD_CONNECT", "CLIENT_MUTE", "SEVEN_DAYS_TO_DIE_CLIENT_MUTE",
    "CLIENT_MUTE_TIMEOUT", "SEVEN_DAYS_TO_DIE_CLIENT_MUTE_TIMEOUT",
    "CLIENT_PLATFORM", "STEAM_COMPAT_CLIENT_INSTALL_PATH",
}
BASE_ENV = {k: v for k, v in os.environ.items() if k not in SCRUB}

# One matching stream (index 7) next to an unrelated one (index 9): the mute
# filter reached through the full launch path must mute the game stream only.
STREAMS_JSON = (
    '[{"index": 7, "properties": {"application.name": "7DaysToDie"}},\n'
    ' {"index": 9, "properties": {"application.name": "spotify"}}]\n'
)

PACTL_STUB = """case "$1" in
\t-f) cat "${PACTL_JSON:?}" ;;
\tset-sink-input-mute) printf '%s\\n' "$*" >>"${PACTL_LOG:?}" ;;
\t*) echo "unexpected pactl call: $*" >&2; exit 1 ;;
esac
"""

STEAM_STUB = """printf '%s\\n' "$@" > "$STEAM_ARGV"
printenv 7DTD_CONNECT > "$STEAM_ENV_CONNECT"
"""

# Last-resort guard: any launch that reaches for the host's real `steam` would
# bootstrap a full Steam client into the fake HOME (gigabytes, minutes) before
# this suite could notice. The guard fails fast instead; tests that exercise
# the fallback put their own recording stub earlier on PATH.
STEAM_GUARD_EXIT_STATUS = 99
STEAM_GUARD_STUB = f"""echo \"guard: unexpected host steam launch: $*\" >&2
exit {STEAM_GUARD_EXIT_STATUS}
"""


def _write_executable(path: Path, body: str) -> None:
    path.write_text(f"#!/usr/bin/env bash\n{body}", encoding="utf-8")
    path.chmod(path.stat().st_mode | stat.S_IEXEC)


def _setup(
    tmp_path: Path,
    *,
    game_dir: Path | None = None,
    game_run_seconds: float = 0,
) -> Path:
    """Fake install: game dir + platform.cfg + exe stub recording its argv."""
    game = game_dir if game_dir is not None else tmp_path / "game"
    game.mkdir(parents=True, exist_ok=True)
    (game / "platform.cfg").write_text(STEAM_CFG, encoding="utf-8")
    record = tmp_path / "game-argv.txt"
    # Proton runs the exe with cwd=game and passes extra args through. The
    # optional sleep keeps the launcher alive long enough that backgrounded
    # children (the mute poller) finish their first poll deterministically.
    body = f"printf '%s\\n' \"$@\" > {shlex.quote(str(record))}\n"
    if game_run_seconds > 0:
        body += f"sleep {game_run_seconds}\n"
    _write_executable(game / "7DaysToDie.exe", body)
    _write_executable(tmp_path / "proton-stub", "shift\nexec \"$@\"\n")
    return game


def _launch_env(
    tmp_path: Path,
    *,
    local_platform: bool = True,
    connect: str | None = None,
    mute: bool = False,
    extra_env: dict[str, str | None] | None = None,
) -> dict[str, str]:
    """Build the sandboxed env for a launcher run; see _launch."""
    env = {
        **BASE_ENV,
        "GAME": str(tmp_path / "game"),
        "HOME": str(tmp_path / "home"),
    }
    # mute=False pins CLIENT_MUTE=0 so tests stay quiet; mute=True leaves the
    # variable unset so the launcher's default-on behavior is what runs.
    if not mute:
        env["CLIENT_MUTE"] = "0"
    if local_platform:
        env["CLIENT_PLATFORM"] = "local"
    if connect is not None:
        env["7DTD_CONNECT"] = connect
    # Default to the direct-Proton branch unless the test pins PROTON itself
    # (usually to None): the launcher only reaches the steam -applaunch
    # fallback when no Proton is found, and with HOME redirected that fallback
    # would exec the host's real steam client.
    if extra_env is None or "PROTON" not in extra_env:
        env.setdefault("PROTON", str(tmp_path / "proton-stub"))
        compat = tmp_path / "compat"
        compat.mkdir(exist_ok=True)
        env.setdefault("COMPAT", str(compat))
    if extra_env is not None:
        for key, value in extra_env.items():
            if value is None:
                env.pop(key, None)
            else:
                env[key] = value
    guard_bin = tmp_path / "guard-bin"
    guard_bin.mkdir(exist_ok=True)
    _write_executable(guard_bin / "steam", STEAM_GUARD_STUB)
    # PATH is rebuilt, not extended: a test's stub dir first, then the guard,
    # then the host PATH. Extending the inherited PATH would leave the system
    # dirs (and the real steam) ahead of the guard, making it dead weight.
    stub_dirs = (extra_env or {}).get("PATH") or ""
    parts = [stub_dirs, str(guard_bin), BASE_ENV.get("PATH", "")]
    env["PATH"] = os.pathsep.join(p for p in parts if p)
    return env


def _launch(
    tmp_path: Path,
    *,
    local_platform: bool = True,
    connect: str | None = None,
    mute: bool = False,
    extra_env: dict[str, str | None] | None = None,
) -> subprocess.CompletedProcess[str]:
    """Run the launcher sandboxed; a None extra_env value omits the variable
    entirely (e.g. PROTON unset selects the steam -applaunch fallback or
    COMPAT derivation from GAME's library)."""
    return subprocess.run(
        ["bash", str(LAUNCH)],
        env=_launch_env(
            tmp_path,
            local_platform=local_platform,
            connect=connect,
            mute=mute,
            extra_env=extra_env,
        ),
        capture_output=True, text=True,
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


def test_invalid_platform_value_leaves_config_alone(tmp_path: Path) -> None:
    """Only 1/local/Local/LAN select Local; anything else must be ignored."""
    game = _setup(tmp_path)
    r = _launch(tmp_path, local_platform=False,
                extra_env={"CLIENT_PLATFORM": "bogus"})
    assert r.returncode == 0, r.stderr
    assert (game / "platform.cfg").read_text(encoding="utf-8") == STEAM_CFG
    assert not (game / "platform.cfg.re-localbak").exists()


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


@pytest.mark.skipif(os.getuid() == 0, reason="root ignores dir permissions")
def test_failed_setup_restores_swapped_platform(tmp_path: Path) -> None:
    """A failure AFTER the Local-platform swap must still reach the exit traps.
    The traps used to be installed after swap_local_platform, so a set -e abort
    in between (here: mkdir -p of LOGDIR under a read-only COMPAT) left the
    game install swapped to the Local platform with no restore until the next
    launch self-healed it."""
    game = _setup(tmp_path)
    compat = tmp_path / "compat-ro"
    compat.mkdir()
    compat.chmod(0o500)  # -d passes; mkdir beneath fails for non-root
    try:
        r = _launch(tmp_path, extra_env={"COMPAT": str(compat)})
    finally:
        compat.chmod(0o700)
    assert r.returncode != 0, r.stdout + r.stderr
    assert (game / "platform.cfg").read_text(encoding="utf-8") == STEAM_CFG
    assert not (game / "platform.cfg.re-localbak").exists()


def _game_exe_pids() -> list[str]:
    """PIDs of running stub game exes (matched by exe name, like the harnesses)."""
    out = subprocess.run(
        ["pgrep", "-f", r"[/]7DaysToDie\.exe"],
        capture_output=True, text=True, check=False,
    )
    return out.stdout.split()


def test_sigterm_runs_cleanup_traps(tmp_path: Path) -> None:
    """TERM must not kill the launcher dead where it stands: one_shot_join.sh
    stops launchers with TERM, so the exit traps have to run (restore
    platform.cfg) and the exit status must propagate as 143. The TERM is also
    forwarded to the waited game child: a launcher that exited while Proton
    kept running would orphan the wine stack."""
    game = _setup(tmp_path, game_run_seconds=30)
    env = _launch_env(tmp_path)
    # Pipes would be held open by the stub game if it outlived the launcher,
    # deadlocking communicate/wait; devnull avoids that. A new session keeps
    # the game subtree killable as a unit.
    proc = subprocess.Popen(
        ["bash", str(LAUNCH)], env=env,
        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL, start_new_session=True,
    )
    try:
        time.sleep(1.5)
        proc.terminate()
        proc.wait(timeout=15)
        assert proc.returncode == TERM_EXIT_STATUS, proc.returncode
        # The EXIT trap restored the original config.
        assert (game / "platform.cfg").read_text(encoding="utf-8") == STEAM_CFG
        assert not (game / "platform.cfg.re-localbak").exists()
        # The forwarded TERM took the stub game down with the launcher.
        deadline = time.monotonic() + 10
        while _game_exe_pids():
            assert time.monotonic() < deadline, "stub game survived launcher TERM"
            time.sleep(0.1)
    finally:
        # Belt and braces: reap anything left in the session (e.g. an orphaned
        # sleep grandchild of the stub) so nothing outlives the test.
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass


def _mute_poller_pids(timeout_arg: str) -> list[str]:
    """PIDs of running mute_client_audio.sh pollers started with timeout_arg."""
    out = subprocess.run(
        ["pgrep", "-f", f"mute_client_audio.sh {timeout_arg}"],
        capture_output=True, text=True, check=False,
    )
    return out.stdout.split()


def test_sigterm_does_not_orphan_mute_poller(tmp_path: Path) -> None:
    """The default launch (no platform swap) previously had no traps at all,
    so TERM killed the launcher outright and left the mute poller running its
    full window. TERM must now reap the poller via the shared exit path."""
    _setup(tmp_path, game_run_seconds=30)
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    # pactl lists no streams, jq prints nothing: the helper keeps polling
    # until its deadline, which is what the launcher must cut short.
    _write_executable(bin_dir / "pactl", "echo '[]'\n")
    _write_executable(bin_dir / "jq", "exit 0\n")
    env = _launch_env(
        tmp_path,
        local_platform=False, mute=True,
        extra_env={"PATH": str(bin_dir), "CLIENT_MUTE_TIMEOUT": "300"},
    )
    proc = subprocess.Popen(
        ["bash", str(LAUNCH)], env=env,
        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL, start_new_session=True,
    )
    try:
        # Wait until the poller is actually up before signalling, so the test
        # cannot race the launcher's own startup ordering.
        deadline = time.monotonic() + 10
        while not _mute_poller_pids("300"):
            assert time.monotonic() < deadline, "mute poller never started"
            assert proc.poll() is None, "launcher exited early"
            time.sleep(0.1)
        proc.terminate()
        proc.wait(timeout=15)
        assert proc.returncode == TERM_EXIT_STATUS, proc.returncode
        deadline = time.monotonic() + 10
        while _mute_poller_pids("300"):
            assert time.monotonic() < deadline, "mute poller survived TERM"
            time.sleep(0.1)
    finally:
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass


def test_connect_env_forwards_connect_arg(tmp_path: Path) -> None:
    _setup(tmp_path)
    r = _launch(tmp_path, connect="127.0.0.1:27025")
    assert r.returncode == 0, r.stderr
    argv = _argv(tmp_path)
    assert "-connect=127.0.0.1:27025" in argv
    assert "-skipintro" in argv


def test_eac_off_and_render_flags_reach_game(tmp_path: Path) -> None:
    """EAC off is mandatory for any C# client mod (AGENTS.md rule 1); the
    render/log flags are the launcher's contract with the game process."""
    _setup(tmp_path)
    r = _launch(tmp_path)
    assert r.returncode == 0, r.stderr
    argv = _argv(tmp_path)
    assert "-noeac" in argv
    assert "-force-d3d11" in argv
    assert "-nogs" in argv
    assert "-logfile" in argv


def test_compat_derives_from_game_library(tmp_path: Path) -> None:
    """A game installed under a second-disk library gets its Proton prefix
    (<library>/compatdata/<appid>) derived from GAME, so the direct-Proton
    branch keeps running instead of degrading to steam -applaunch."""
    game = tmp_path / "library" / "steamapps" / "common" / "Game"
    _setup(tmp_path, game_dir=game)
    # The launcher's search looks next to GAME's library; plant a usable
    # Proton exactly where "${GAME%/common/*}/common/Proton - Experimental"
    # resolves, and pre-create the derived compatdata dir it checks with -d.
    library_root = tmp_path / "library" / "steamapps"
    proton = library_root / "common" / "Proton - Experimental" / "proton"
    proton.parent.mkdir(parents=True)
    proton.write_text("#!/usr/bin/env bash\nshift\nexec \"$@\"\n", encoding="utf-8")
    proton.chmod(proton.stat().st_mode | stat.S_IEXEC)
    (library_root / "compatdata" / "251570").mkdir(parents=True)
    r = _launch(tmp_path, extra_env={"GAME": str(game), "COMPAT": None})
    assert r.returncode == 0, r.stderr
    # Log dir under the DERIVED prefix proves which COMPAT was used.
    logdir = (library_root / "compatdata" / "251570"
              / "pfx" / "drive_c" / "users" / "steamuser" / "AppData"
              / "Roaming" / "7DaysToDie" / "logs")
    assert logdir.is_dir(), r.stdout
    # Direct-Proton branch taken, not the steam fallback.
    assert "Proton:" in r.stdout
    # The derived branch must still forward the launcher's contract flags.
    argv = _argv(tmp_path)
    assert "-noeac" in argv
    assert "-force-d3d11" in argv
    assert "-skipintro" in argv


def test_steam_fallback_keeps_connect_and_env(tmp_path: Path) -> None:
    """Without a usable Proton the steam -applaunch fallback must still carry
    -noeac, the forwarded -connect= arg, and the canonical 7DTD_CONNECT env."""
    _setup(tmp_path)
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    steam_argv = tmp_path / "steam-argv.txt"
    steam_env = tmp_path / "steam-connect-env.txt"
    _write_executable(bin_dir / "steam", STEAM_STUB)
    r = _launch(
        tmp_path,
        connect="127.0.0.1:27025",
        # This test IS the fallback: PROTON pinned to None skips the default
        # injection, and its recording stub shadows the guard on PATH.
        extra_env={
            "PATH": str(bin_dir),
            "STEAM_ARGV": str(steam_argv),
            "STEAM_ENV_CONNECT": str(steam_env),
            "PROTON": None,
            "COMPAT": None,
        },
    )
    assert r.returncode == 0, r.stderr
    argv = steam_argv.read_text(encoding="utf-8").splitlines()
    assert argv == [
        "-applaunch", "251570", "-noeac",
        "-skipintro", "-SkipNewsScreen=true",
        "-connect=127.0.0.1:27025",
    ]
    # Steam does not reliably pass -connect=; the env is the reliable channel.
    assert steam_env.read_text(encoding="utf-8").strip() == "127.0.0.1:27025"


def test_host_steam_is_never_reached(tmp_path: Path) -> None:
    """The fallback with no stub steam must hit the guard, not the host client:
    a real steam here bootstraps gigabytes into the fake HOME and opens a
    window on the developer's desktop."""
    _setup(tmp_path)
    r = _launch(tmp_path, extra_env={"PROTON": None, "COMPAT": None})
    assert "guard: unexpected host steam launch" in r.stderr, r.stderr
    # The guard's exit status must propagate through the launcher, not be
    # swallowed: a silent 0 would make the fallback look like it launched.
    assert r.returncode == STEAM_GUARD_EXIT_STATUS, r.returncode


@pytest.mark.skipif(shutil.which("jq") is None, reason="mute filter needs jq")
def test_default_mute_mutes_game_stream_via_launch(tmp_path: Path) -> None:
    """Default-on mute end to end: the poller started by launch_client must
    mute the game's sink input and leave unrelated streams alone."""
    _setup(tmp_path, game_run_seconds=2)
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    streams = tmp_path / "streams.json"
    streams.write_text(STREAMS_JSON, encoding="utf-8")
    mute_log = tmp_path / "mute.log"
    mute_log.touch()
    _write_executable(bin_dir / "pactl", PACTL_STUB)
    r = _launch(
        tmp_path,
        mute=True,
        extra_env={
            "PATH": str(bin_dir),
            "PACTL_JSON": str(streams),
            "PACTL_LOG": str(mute_log),
        },
    )
    assert r.returncode == 0, r.stderr
    assert "Client mute: on" in r.stdout
    muted = mute_log.read_text(encoding="utf-8").splitlines()
    assert "set-sink-input-mute 7 1" in muted
    assert "set-sink-input-mute 9 1" not in muted


def test_client_mute_opt_out_never_invokes_pactl(tmp_path: Path) -> None:
    _setup(tmp_path)
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    mute_log = tmp_path / "mute.log"
    mute_log.touch()
    # Any invocation would append; the opt-out must prevent them all.
    _write_executable(
        bin_dir / "pactl",
        f"printf '%s\\n' \"$*\" >>{shlex.quote(str(mute_log))}\n",
    )
    r = _launch(
        tmp_path,
        extra_env={"PATH": str(bin_dir), "CLIENT_MUTE": "0"},
    )
    assert r.returncode == 0, r.stderr
    assert "Client mute" not in r.stdout
    assert mute_log.read_text(encoding="utf-8") == ""


PROTON_PATHS = ROOT / "scripts" / "proton_paths.sh"


def _resolve_compat(game: str, appid: str, steam_root: str, compat: str = "") -> str:
    """Run scripts/proton_paths.sh resolve_compat in a clean bash."""
    script = (
        f"source {shlex.quote(str(PROTON_PATHS))} && resolve_compat "
        + " ".join(shlex.quote(a) for a in (game, appid, steam_root, compat))
    )
    return subprocess.run(
        ["bash", "-c", script],
        capture_output=True, text=True, check=True, timeout=30,
    ).stdout.strip()


def test_resolve_compat_derives_second_library_prefix() -> None:
    """The launcher writes its log under GAME's own library prefix and
    one_shot_join.sh reads it back through the same helper: a second-disk
    install must resolve to <library>/compatdata/<appid>, not the default
    root, or the join poll watches an empty file and reports timeouts."""
    assert _resolve_compat(
        "/disks/b/steamapps/common/7 Days To Die", "251570", "/home/u/.local/share/Steam",
    ) == "/disks/b/steamapps/compatdata/251570"


def test_resolve_compat_explicit_override_wins() -> None:
    assert _resolve_compat(
        "/disks/b/steamapps/common/Game", "251570", "/home/u/.local/share/Steam",
        "/custom/compat",
    ) == "/custom/compat"


def test_resolve_compat_falls_back_to_steam_root() -> None:
    assert _resolve_compat("/opt/games/Game", "251570", "/steamroot") == (
        "/steamroot/steamapps/compatdata/251570"
    )


def test_harnesses_resolve_log_prefix_through_shared_helper() -> None:
    """The join harnesses must take the client-log prefix from proton_paths.sh,
    never from their own copy of the rule: duplicated derivation is exactly how
    second-disk installs drifted into polling a log the launcher never wrote."""
    for name in ("one_shot_join.sh", "zero_nre_join_loop.sh"):
        src = (ROOT / "scripts" / name).read_text(encoding="utf-8")
        assert "proton_paths.sh" in src, name
        assert "resolve_compat" in src, name
