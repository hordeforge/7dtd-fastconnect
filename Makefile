# The repo root is this Makefile's own directory, not the caller's cwd, so
# `make -f /path/Makefile` from elsewhere builds the same tree and (because
# every dotnet call below runs from ROOT) resolves the same global.json, so
# the same SDK band, as `make -C`.
ROOT := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
GAME ?= $(HOME)/.local/share/Steam/steamapps/common/7 Days To Die
MOD_NAME := 7dtd-fastconnect
DIST := $(ROOT)/dist/$(MOD_NAME)
# Overridable so the mod can be installed into the per-user Mods directory
# (~/.../AppData/Roaming/7DaysToDie/Mods under Proton), which the game also
# loads. Projects that treat the game install as read-only reference need that.
MODS_DIR ?= $(GAME)/Mods
INSTALL_DIR := $(MODS_DIR)/$(MOD_NAME)

# Every Python file in the repo, so a new one is type-checked the day it lands
# instead of the day someone remembers to add it here.
PY_SOURCES := $(wildcard scripts/*.py)

# Only honor candidate roots that actually contain a dotnet SDK (an sdk/
# subdir): pointing DOTNET_ROOT/PATH at a runtime-only install breaks SDK
# resolution instead of falling through to the system dotnet. The required
# band itself is pinned by global.json at the repo root.
DOTNET_ROOT ?= $(firstword \
  $(foreach d,$(HOME)/.cache/dotnet-sdk $(HOME)/.dotnet /usr/lib/dotnet, \
    $(if $(wildcard $(d)/sdk/*),$(d))))
ifneq ($(DOTNET_ROOT),)
  export DOTNET_ROOT
  export PATH := $(DOTNET_ROOT):$(PATH)
endif

.PHONY: build install uninstall clean test gate coverage package help dotnet-version check-mods-dir check-game-root doctor

# The SDK the build actually resolved under the DOTNET_ROOT search above, so
# the package build record names the compiler instead of whatever dotnet the
# caller's PATH happens to hold.
dotnet-version:
	@dotnet --version

# One way to invoke pytest, shared by `make test` and `make gate` so the two
# cannot drift: the pinned dev toolchain through uv, else the pytest on PATH
# once assert_tool_pin.sh proves it reports the == pin in pyproject.toml.
# --locked, not --frozen: --frozen only promises to leave uv.lock alone, so a
# pin edited in pyproject.toml without a re-lock silently keeps installing the
# versions the lock still names, and the gate runs a toolchain no file in the
# tree declares. --locked fails that run instead, which is the same
# RestoreLockedMode the C# build already runs with.
ifeq ($(shell command -v uv 2>/dev/null),)
PYTEST := $(ROOT)/scripts/assert_tool_pin.sh pytest python3 -m pytest
else
PYTEST := uv run --locked --group dev -- pytest
endif

# One gate on its own, so an edit to one script is checked in seconds instead
# of a full `make test` run. A .py gate is a pytest module with no __main__: it
# is dispatched to pytest, never executed as a program (running it directly
# printed nothing and exited 0, a green that tested nothing). GATE_ARGS passes
# extra arguments through, so one test is
#   make gate GATE=scripts/test_launch_client_platform.py GATE_ARGS=-kplatform
# (`-kplatform` without a space: make splits the value on whitespace).
GATE ?=
GATE_ARGS ?=
gate:
	@test -n "$(GATE)" || { echo "usage: make gate GATE=scripts/test_<name>.sh" >&2; exit 2; }
	@test -f "$(ROOT)/$(GATE)" || { echo "no such gate: $(GATE)" >&2; exit 2; }
	@echo "gate: $(GATE)"
	@case "$(GATE)" in \
	*.py) cd "$(ROOT)" && $(PYTEST) "$(GATE)" $(GATE_ARGS) ;; \
	*) test -x "$(ROOT)/$(GATE)" || { echo "not executable: $(GATE)" >&2; exit 2; }; \
	   "$(ROOT)/$(GATE)" ;; \
	esac

help:
	@echo "targets:"
	@echo "  test       every gate CI runs (offline; the full local verification)"
	@echo "  gate       one gate: make gate GATE=scripts/test_<name>.sh (a .py gate"
	@echo "             goes to pytest; GATE_ARGS=-k<expr> selects one test)"
	@echo "  doctor     check the toolchain \`make test\` needs, naming what is missing"
	@echo "  check-game-root  check the game install build/install/package compile against"
	@echo "  build      build the mod DLL into dist/ (needs the game install)"
	@echo "  install    build, then copy into \$$GAME/Mods/7dtd-fastconnect"
	@echo "  uninstall  remove that installed copy"
	@echo "  package    build and zip dist/7dtd-fastconnect-<tag>.zip"
	@echo "  coverage   line coverage of ConnectTarget plus the rendered badge"
	@echo "  clean      remove dist/ and the C# bin/ and obj/ trees"
	@echo "  dotnet-version  print the dotnet SDK version the build would use"
	@echo "setup: make doctor (what is missing); uv sync --locked --group dev for the"
	@echo "        pinned ruff/mypy/pytest/yamllint; dotnet SDK band in global.json"

# Runs dotnet from ROOT so global.json (the SDK pin) is always the one in this
# tree, and with the C locale and UTC so no host locale or timezone can reach
# the compiler or the resources it embeds.
build: check-game-root
	cd "$(ROOT)" && LC_ALL=C TZ=UTC dotnet build \
		"Source/ConnectMod/ConnectMod.csproj" -c Release -v q \
		-p:RestoreLockedMode=true \
		-p:GameRoot="$(GAME)"
	# LICENSE rides along with the payload: the mod ships as a redistributable
	# zip, so its terms have to travel with it.
	cp -f "$(ROOT)/ModInfo.xml" "$(ROOT)/LICENSE" "$(DIST)/"
	@echo "OK → $(DIST)"

# Line coverage of the ConnectTarget offline gate compiled with the dotnet
# SDK (scripts/coverage-cs.sh mirrors scripts/test_connect_target_parse.sh);
# the badge filters to /Source/ so stub and harness lines stay out.
coverage:
	$(ROOT)/scripts/coverage-cs.sh
	# coverage-cs.sh skips with status 0 when dotnet or dotnet-coverage is
	# absent, so a missing report is the skip reaching the badge step, and the
	# renderer's own answer names a missing coverage report instead of the
	# missing tool. Check here so the run says which, and so the badge is never
	# rendered from a report an earlier run left behind.
	@test -f "$(ROOT)/coverage.cobertura.xml" || { \
	  echo "ERROR: scripts/coverage-cs.sh wrote no report; it skips with status 0" \
	    "when dotnet or dotnet-coverage is absent. Run 'dotnet tool restore" \
	    "--tool-path <dir>' (version pinned in .config/dotnet-tools.json)." >&2; \
	  exit 1; \
	}
	cd "$(ROOT)" && uv run --locked --group dev python scripts/coverage_badge.py \
		coverage.svg "/Source/" coverage.cobertura.xml

# The offline gate scripts, in run order. Explicit rather than a
# scripts/test_*.sh wildcard: test_common.sh matches that glob but is sourced
# plumbing, not a gate, and a wildcard would run it as one.
GATES := \
	scripts/test_connect_target_parse.sh \
	scripts/test_repro_zip.sh \
	scripts/test_stage_mod.sh \
	scripts/test_package_verify.sh \
	scripts/test_player_name_override.sh \
	scripts/test_force_load_sync_override.sh \
	scripts/test_automation_mode.sh \
	scripts/test_local_host_world_load.sh \
	scripts/test_eula_gate_once.sh \
	scripts/test_auto_join_latch.sh \
	scripts/test_mute_client_audio.sh \
	scripts/test_config_validate.sh \
	scripts/test_unmute_client_audio.sh \
	scripts/test_cli_help.sh \
	scripts/test_coverage_badge.sh \
	scripts/test_monotonic_deadlines.sh \
	scripts/test_log_marker_cache.sh \
	scripts/test_log_marker_fuzz.sh \
	scripts/test_log_sanitize.sh \
	scripts/test_log_sanitize_fuzz.sh \
	scripts/test_log_writer_sanitize.sh \
	scripts/test_join_evidence.sh \
	scripts/test_version_sync.sh \
	scripts/test_changelog_gate.sh \
	scripts/test_cycle_filename_guard.sh \
	scripts/test_one_shot_launcher_group.sh \
	scripts/test_zero_nre_log_dir_guard.sh \
	scripts/test_zero_nre_verdict_reset.sh \
	scripts/test_zero_nre_server_stop.sh \
	scripts/test_make_tool_pin.sh \
	scripts/test_tool_manifest.sh \
	scripts/test_prereqs.sh \
	scripts/test_game_root_preflight.sh

# The toolchain the gates below need, before any of them runs: a gate that
# cannot find zip or jq exits 0, so without this the suite reports a green on a
# machine where those gates never ran.
test:
	@$(ROOT)/scripts/check_prereqs.sh
	@for gate in $(GATES); do "$(ROOT)/$$gate" || exit $$?; done
	# Without uv the Python gates below run a binary from PATH, so
	# assert_tool_pin.sh checks each one against its == pin in pyproject.toml
	# first: a fallback run must be the pinned tool, not merely a tool.
	# shellcheck is a system binary, not a uv dependency, so it cannot be
	# pinned the way yamllint below is. What it can do is fail loud: a missing
	# shellcheck used to print a WARN and continue, so the run that reports
	# "make test passed" proved nothing about the shell sources on a machine
	# without it. Same reasoning as the yamllint comment below.
	@if command -v shellcheck >/dev/null; then \
	  echo "shellcheck:"; \
	  shellcheck -S warning $(ROOT)/scripts/*.sh; \
	else \
	  echo "ERROR: shellcheck not installed; shell sources went unlinted" >&2; \
	  exit 1; \
	fi
	# yamllint joins the Python gates rather than staying a bare `command -v`
	# probe: the hosted CI runner has no yamllint on PATH, so a probe-only gate
	# printed a WARN and linted nothing there. Pinned in [dependency-groups]
	# dev, so `uv run --locked` gives CI the same version a maintainer gets.
	@if command -v uv >/dev/null; then \
	  echo "yamllint:"; \
	  cd "$(ROOT)" && uv run --locked --group dev yamllint . && \
	  uv run --locked --group dev ruff check scripts && \
	  uv run --locked --group dev ruff format --check scripts && \
	  uv run --locked --group dev mypy --strict $(PY_SOURCES); \
	elif command -v ruff >/dev/null && command -v mypy >/dev/null && \
	     command -v yamllint >/dev/null; then \
	  cd "$(ROOT)" && "$(ROOT)/scripts/assert_tool_pin.sh" yamllint yamllint && \
	  "$(ROOT)/scripts/assert_tool_pin.sh" ruff ruff && \
	  "$(ROOT)/scripts/assert_tool_pin.sh" mypy mypy && \
	  yamllint . && \
	  ruff check scripts && ruff format --check scripts && \
	  mypy --strict $(PY_SOURCES); \
	else \
	  echo "ERROR: neither uv nor ruff+mypy+yamllint available; run 'uv sync --group dev' first" >&2; \
	  exit 1; \
	fi
	@cd "$(ROOT)" && $(PYTEST) scripts/test_launch_client_platform.py -q --tb=short

package:
	$(ROOT)/scripts/package.sh

install: build
	@$(MAKE) --no-print-directory check-mods-dir
	mkdir -p "$(INSTALL_DIR)"
	cp -f "$(DIST)/ModInfo.xml" "$(DIST)/LICENSE" "$(DIST)/7dtd-fastconnect.dll" "$(INSTALL_DIR)/"
	@echo "Installed → $(INSTALL_DIR)"
	@echo "Launch client with EAC off (-noeac). Example:"
	@echo "  env 7DTD_CONNECT=127.0.0.1:27025 $(ROOT)/scripts/launch_client.sh"

uninstall:
	@$(MAKE) --no-print-directory check-mods-dir
	rm -rf "$(INSTALL_DIR)"
	@echo "Removed $(INSTALL_DIR)"

# An empty or root-level MODS_DIR (an unset variable, an export that did not
# survive, a typo like MODS_DIR=) makes INSTALL_DIR "/7dtd-fastconnect", and
# `make uninstall` would rm -rf that. Refuse before either target touches
# disk rather than after.
check-mods-dir:
	@if [ -z "$(MODS_DIR)" ] || [ "$(MODS_DIR)" = / ]; then \
		echo "ERROR: MODS_DIR is '$(MODS_DIR)'; point it at the game's Mods directory (GAME=... or MODS_DIR=...)" >&2; \
		exit 2; \
	fi
	@if [ "$(INSTALL_DIR)" != /*/* ]; then \
		echo "ERROR: INSTALL_DIR '$(INSTALL_DIR)' is not two levels below /; refusing to touch it" >&2; \
		exit 2; \
	fi

# The game install is the one requirement `make test` and `make doctor` do not
# need, so it is checked here rather than in check_prereqs.sh, which has to
# stay runnable on a machine without the game. A HintPath that resolves to
# nothing is not a build error: the compile then fails with one CS0246 per
# game type the mod touches, which reads as broken source rather than as a
# missing install.
check-game-root:
	@"$(ROOT)/scripts/check_game_root.sh" "$(GAME)"

clean:
	rm -rf "$(ROOT)/dist" "$(ROOT)/Source/ConnectMod/bin" "$(ROOT)/Source/ConnectMod/obj"

# What `make test` needs on this machine, before spending 70s finding out. Same
# check the test target runs first, so this answer cannot drift from it.
doctor:
	@$(ROOT)/scripts/check_prereqs.sh
