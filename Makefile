ROOT := $(CURDIR)
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

.PHONY: build install uninstall clean test gate coverage package help dotnet-version

# The SDK the build actually resolved under the DOTNET_ROOT search above, so
# the package build record names the compiler instead of whatever dotnet the
# caller's PATH happens to hold.
dotnet-version:
	@dotnet --version

# One gate on its own, so an edit to one script is checked in seconds instead
# of a full `make test` run. The Python gate is a pytest module rather than a
# shell gate; run it with
#   uv run --frozen --group dev pytest scripts/test_launch_client_platform.py
GATE ?=
gate:
	@test -n "$(GATE)" || { echo "usage: make gate GATE=scripts/test_<name>.sh" >&2; exit 2; }
	@test -f "$(ROOT)/$(GATE)" || { echo "no such gate: $(GATE)" >&2; exit 2; }
	@echo "gate: $(GATE)"
	"$(ROOT)/$(GATE)"

help:
	@echo "targets:"
	@echo "  test       every gate CI runs (offline; the full local verification)"
	@echo "  gate       one shell gate: make gate GATE=scripts/test_<name>.sh"
	@echo "  build      build the mod DLL into dist/ (needs the game install)"
	@echo "  install    build, then copy into \$$GAME/Mods/7dtd-fastconnect"
	@echo "  uninstall  remove that installed copy"
	@echo "  package    build and zip dist/7dtd-fastconnect-<tag>.zip"
	@echo "  coverage   line coverage of ConnectTarget plus the rendered badge"
	@echo "  clean      remove dist/ and the C# bin/ and obj/ trees"
	@echo "python gate (not a shell gate):"
	@echo "  uv run --frozen --group dev pytest scripts/test_launch_client_platform.py"
	@echo "setup: uv sync --group dev (pinned ruff/mypy/pytest/yamllint); dotnet SDK band in global.json"

build:
	dotnet build "$(ROOT)/Source/ConnectMod/ConnectMod.csproj" -c Release -v q \
		-p:RestoreLockedMode=true \
		-p:GameRoot="$(GAME)"
	cp -f "$(ROOT)/ModInfo.xml" "$(DIST)/"
	@echo "OK → $(DIST)"

# Line coverage of the ConnectTarget offline gate compiled with the dotnet
# SDK (scripts/coverage-cs.sh mirrors scripts/test_connect_target_parse.sh);
# the badge filters to /Source/ so stub and harness lines stay out.
coverage:
	$(ROOT)/scripts/coverage-cs.sh
	cd "$(ROOT)" && uv run --frozen --group dev python scripts/coverage_badge.py \
		coverage.svg "/Source/" coverage.cobertura.xml

# The offline gate scripts, in run order. Explicit rather than a
# scripts/test_*.sh wildcard: test_common.sh matches that glob but is sourced
# plumbing, not a gate, and a wildcard would run it as one.
GATES := \
	scripts/test_connect_target_parse.sh \
	scripts/test_repro_zip.sh \
	scripts/test_stage_mod.sh \
	scripts/test_player_name_override.sh \
	scripts/test_force_load_sync_override.sh \
	scripts/test_automation_mode.sh \
	scripts/test_local_host_world_load.sh \
	scripts/test_eula_gate_once.sh \
	scripts/test_mute_client_audio.sh \
	scripts/test_config_validate.sh \
	scripts/test_unmute_client_audio.sh \
	scripts/test_cli_help.sh \
	scripts/test_monotonic_deadlines.sh \
	scripts/test_log_marker_cache.sh \
	scripts/test_log_marker_fuzz.sh \
	scripts/test_log_sanitize.sh \
	scripts/test_version_sync.sh \
	scripts/test_cycle_filename_guard.sh \
	scripts/test_zero_nre_log_dir_guard.sh \
	scripts/test_zero_nre_server_stop.sh \
	scripts/test_make_tool_pin.sh

test:
	@for gate in $(GATES); do "$(ROOT)/$$gate" || exit $$?; done
	# Without uv the Python gates below run a binary from PATH, so
	# assert_tool_pin.sh checks each one against its == pin in pyproject.toml
	# first: a fallback run must be the pinned tool, not merely a tool.
	@if command -v shellcheck >/dev/null; then \
	  echo "shellcheck:"; \
	  shellcheck -S warning $(ROOT)/scripts/*.sh; \
	else \
	  echo "WARN: shellcheck not installed; shell lint skipped" >&2; \
	fi
	# yamllint joins the Python gates rather than staying a bare `command -v`
	# probe: the hosted CI runner has no yamllint on PATH, so a probe-only gate
	# printed a WARN and linted nothing there. Pinned in [dependency-groups]
	# dev, so `uv run --frozen` gives CI the same version a maintainer gets.
	@if command -v uv >/dev/null; then \
	  echo "yamllint:"; \
	  cd "$(ROOT)" && uv run --frozen --group dev yamllint . && \
	  uv run --frozen --group dev ruff check scripts && \
	  uv run --frozen --group dev ruff format --check scripts && \
	  uv run --frozen --group dev mypy --strict $(PY_SOURCES); \
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
	@if command -v uv >/dev/null; then \
	  cd "$(ROOT)" && uv run --frozen --group dev pytest scripts/test_launch_client_platform.py -q --tb=short; \
	else \
	  cd "$(ROOT)" && "$(ROOT)/scripts/assert_tool_pin.sh" pytest python3 -m pytest && \
	  python3 -m pytest scripts/test_launch_client_platform.py -q --tb=short; \
	fi

package:
	$(ROOT)/scripts/package.sh

install: build
	mkdir -p "$(INSTALL_DIR)"
	cp -f "$(DIST)/ModInfo.xml" "$(DIST)/7dtd-fastconnect.dll" "$(INSTALL_DIR)/"
	@echo "Installed → $(INSTALL_DIR)"
	@echo "Launch client with EAC off (-noeac). Example:"
	@echo "  env 7DTD_CONNECT=127.0.0.1:27025 $(ROOT)/scripts/launch_client.sh"

uninstall:
	rm -rf "$(INSTALL_DIR)"
	@echo "Removed $(INSTALL_DIR)"

clean:
	rm -rf "$(ROOT)/dist" "$(ROOT)/Source/ConnectMod/bin" "$(ROOT)/Source/ConnectMod/obj"
