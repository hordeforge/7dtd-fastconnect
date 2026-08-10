ROOT := $(CURDIR)
GAME ?= $(HOME)/.local/share/Steam/steamapps/common/7 Days To Die
MOD_NAME := zdtd-connect
DIST := $(ROOT)/dist/$(MOD_NAME)
INSTALL_DIR := $(GAME)/Mods/$(MOD_NAME)

DOTNET_ROOT ?= $(firstword \
  $(wildcard $(HOME)/.cache/dotnet-sdk) \
  $(wildcard $(HOME)/.dotnet) \
)
ifneq ($(DOTNET_ROOT),)
  export DOTNET_ROOT
  export PATH := $(DOTNET_ROOT):$(PATH)
endif

.PHONY: build install uninstall clean

build:
	dotnet build "$(ROOT)/Source/ConnectMod/ConnectMod.csproj" -c Release -v q \
		-p:GameRoot="$(GAME)"
	cp -f "$(ROOT)/ModInfo.xml" "$(DIST)/"
	@echo "OK → $(DIST)"

install: build
	mkdir -p "$(INSTALL_DIR)"
	cp -f "$(DIST)/ModInfo.xml" "$(DIST)/zdtd-connect.dll" "$(INSTALL_DIR)/"
	@echo "Installed → $(INSTALL_DIR)"
	@echo "Launch client with EAC off (-noeac). Example:"
	@echo "  ZDTD_CONNECT=127.0.0.1:27025 $(ROOT)/scripts/launch_client.sh"

uninstall:
	rm -rf "$(INSTALL_DIR)"
	@echo "Removed $(INSTALL_DIR)"

clean:
	rm -rf "$(ROOT)/dist" "$(ROOT)/Source/ConnectMod/bin" "$(ROOT)/Source/ConnectMod/obj"
