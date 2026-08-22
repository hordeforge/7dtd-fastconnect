ROOT := $(CURDIR)
GAME ?= $(HOME)/.local/share/Steam/steamapps/common/7 Days To Die
MOD_NAME := 7dtd-connect
DIST := $(ROOT)/dist/$(MOD_NAME)
# Overridable so the mod can be installed into the per-user Mods directory
# (~/.../AppData/Roaming/7DaysToDie/Mods under Proton), which the game also
# loads. Projects that treat the game install as read-only reference need that.
MODS_DIR ?= $(GAME)/Mods
INSTALL_DIR := $(MODS_DIR)/$(MOD_NAME)

DOTNET_ROOT ?= $(firstword \
  $(wildcard $(HOME)/.cache/dotnet-sdk) \
  $(wildcard $(HOME)/.dotnet) \
)
ifneq ($(DOTNET_ROOT),)
  export DOTNET_ROOT
  export PATH := $(DOTNET_ROOT):$(PATH)
endif

.PHONY: build install uninstall clean test package

build:
	dotnet build "$(ROOT)/Source/ConnectMod/ConnectMod.csproj" -c Release -v q \
		-p:GameRoot="$(GAME)"
	cp -f "$(ROOT)/ModInfo.xml" "$(DIST)/"
	@echo "OK → $(DIST)"

test:
	$(ROOT)/scripts/test_player_name_override.sh
	$(ROOT)/scripts/test_force_load_sync_override.sh
	$(ROOT)/scripts/test_automation_mode.sh
	$(ROOT)/scripts/test_local_host_world_load.sh
	$(ROOT)/scripts/test_mute_client_audio.sh
	@command -v uv >/dev/null && cd "$(ROOT)" && uv run --with pytest pytest scripts/test_launch_client_platform.py -q --tb=short \
		|| python3 -m pytest scripts/test_launch_client_platform.py -q --tb=short

package:
	$(ROOT)/scripts/package.sh

install: build
	mkdir -p "$(INSTALL_DIR)"
	cp -f "$(DIST)/ModInfo.xml" "$(DIST)/7dtd-connect.dll" "$(INSTALL_DIR)/"
	@echo "Installed → $(INSTALL_DIR)"
	@echo "Launch client with EAC off (-noeac). Example:"
	@echo "  env 7DTD_CONNECT=127.0.0.1:27025 $(ROOT)/scripts/launch_client.sh"

uninstall:
	rm -rf "$(INSTALL_DIR)"
	@echo "Removed $(INSTALL_DIR)"

clean:
	rm -rf "$(ROOT)/dist" "$(ROOT)/Source/ConnectMod/bin" "$(ROOT)/Source/ConnectMod/obj"
