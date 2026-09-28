"""Session setup for the offline gates.

pyproject.toml points pytest's base temp at .scratch/pytest so the fake game
trees stay on disk instead of tmpfs, but pytest creates that directory without
its parents. .scratch is gitignored, so on a fresh checkout every test errors
in setup before a single assertion runs. Creating the parent in pytest_configure
runs before the first tmp_path is materialized.
"""

from __future__ import annotations

import os
from pathlib import Path
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import pytest


def pytest_configure(config: pytest.Config) -> None:
    basetemp: str | Path | None = config.getoption("basetemp", default=None)
    if basetemp is None:
        return
    parent = Path(str(basetemp)).expanduser().absolute().parent
    if str(parent) not in (os.sep, ""):
        parent.mkdir(parents=True, exist_ok=True)
