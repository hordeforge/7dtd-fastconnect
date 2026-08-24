#!/usr/bin/env bash
# Line coverage for the ConnectTarget offline gate, compiled with the dotnet
# SDK instead of mcs so coverlet/dotnet-coverage can instrument it. Mirrors
# scripts/test_connect_target_parse.sh: the same six production sources plus
# the compiler-only stubs and the harness driver, executed once per harness
# mode. Output: coverage.cobertura.xml at the repo root (product sources are
# filtered to /Source/ when the badge renders).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/7dtd-connect-cov.XXXXXX")"
trap 'rm -rf "$work"' EXIT

if ! command -v dotnet >/dev/null 2>&1; then
	echo "SKIP: dotnet SDK not found; cannot run the coverage lane" >&2
	exit 0
fi
if ! command -v dotnet-coverage >/dev/null 2>&1; then
	echo "SKIP: dotnet-coverage not found (dotnet tool install -g dotnet-coverage)" >&2
	exit 0
fi

sources=(
	"$root/Source/ConnectMod/ConnectTarget.cs"
	"$root/Source/ConnectMod/EnvFlags.cs"
	"$root/Source/ConnectMod/ConnectReady.cs"
	"$root/Source/ConnectMod/PlayerNames.cs"
	"$root/Source/ConnectMod/AutomationMode.cs"
	"$root/Source/ConnectMod/BootUnblock.cs"
	"$root/scripts/testdata/connect_target_stubs.cs"
	"$root/scripts/testdata/connect_target_harness.cs"
)

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
	for f in "${sources[@]}"; do echo "    <Compile Include=\"$f\" />"; done
	echo '  </ItemGroup>'
	echo '</Project>'
} > "$work/cov.csproj"

cd "$work"
dotnet build -c Release -v q 2>&1 | tail -1 > /dev/null
dll="$(find bin -name 'cov.dll' | head -1)"

modes=(argv argvenv automation connectready envflags forcesync launchctx parse playernames sanitize)
for m in "${modes[@]}"; do
	dotnet-coverage collect -f cobertura -o "cov-$m.xml" -- dotnet "$dll" "$m" > /dev/null 2>&1 || {
		echo "FAIL: harness mode $m under the coverage profiler" >&2
		exit 1
	}
done

dotnet-coverage merge -f cobertura -o "$root/coverage.cobertura.xml" cov-*.xml > /dev/null
echo "OK: $root/coverage.cobertura.xml"
