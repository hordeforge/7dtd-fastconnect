#!/usr/bin/env bash
# Shared harness-project definition for the offline C# gates. Sourced by
# scripts/coverage-cs.sh and scripts/test_connect_target_parse.sh so both
# compile exactly the same production sources against the compiler-only
# stubs and driver in testdata/; the list can never drift between them.
#
# Callers:
#   harness_sources <repo_root>   -> one absolute source path per line
#   emit_harness_csproj <out.csproj> <repo_root>
#                                 -> net8.0 SDK project compiling those files
#                                    (the SDK path used where mcs is absent:
#                                    coverage instrumentation, CI runners)

harness_sources() {
	local root="$1"
	printf '%s\n' \
		"$root/Source/ConnectMod/ConnectTarget.cs" \
		"$root/Source/ConnectMod/EnvFlags.cs" \
		"$root/Source/ConnectMod/ConnectReady.cs" \
		"$root/Source/ConnectMod/PlayerNames.cs" \
		"$root/Source/ConnectMod/AutomationMode.cs" \
		"$root/Source/ConnectMod/BootUnblock.cs" \
		"$root/scripts/testdata/connect_target_stubs.cs" \
		"$root/scripts/testdata/connect_target_harness.cs"
}

emit_harness_csproj() {
	local out="$1" root="$2"
	{
		echo '<Project Sdk="Microsoft.NET.Sdk">'
		echo '  <PropertyGroup>'
		echo '    <OutputType>Exe</OutputType>'
		echo '    <TargetFramework>net8.0</TargetFramework>'
		echo '    <Nullable>disable</Nullable>'
		echo '    <ImplicitUsings>disable</ImplicitUsings>'
		echo '    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
		echo '  </PropertyGroup>'
		echo '  <ItemGroup>'
		local f
		while IFS= read -r f; do echo "    <Compile Include=\"$f\" />"; done < <(harness_sources "$root")
		echo '  </ItemGroup>'
		echo '</Project>'
	} > "$out"
}
