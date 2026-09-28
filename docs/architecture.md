# Source layout

Two trees carry the project: the C# mod (`Source/ConnectMod/`) and the shell
and Python gates (`scripts/`). Nothing else holds code.

## Source/ConnectMod

One assembly, one namespace (`SdtdConnect`), one file per concern. The game
loads the mod through `IModApi`, so the entry point is fixed by the game's
loader, not by a `Main`: `ModApi.InitMod` is the first thing that runs.

`ModApi.cs` wires everything: it announces the mod, applies prefs, applies the
player-name override, applies every `[HarmonyPatch]` type in the assembly
(skipped unless automation mode is on, for the `AutomationPatchAttribute`
ones), and registers the main-menu handler that starts auto-join.

Dependency direction, from the leaves inward. A file in one row never calls a
file in a row above it.

| Row | Files | Role |
|-----|-------|------|
| Leaf helpers | `LogText.cs`, `TextUtil.cs`, `EnvFlags.cs`, `ProbeFailure.cs`, `ConsoleOutput.cs` | Text sanitizing, code-point counting, env-var truthiness, announce-once failure notes, F1 console writes. No game API, no patches. |
| Flags and identity | `AutomationMode.cs`, `DiagToggle.cs`, `PlayerNames.cs` | Launch-mode and diagnostic switches; the fallback player identity. |
| Boot and gates | `BootUnblock.cs`, `EulaSkip.cs`, `LoadGate.cs`, `ConnectReady.cs` | Frame uncap and sync loading, EULA acceptance, load-gate math, the join gate. |
| Connect | `ConnectTarget.cs` | Target grammar (`TryParse`, `MergePortArg`), launch-context resolution (`7DTD_CONNECT`, `-connect=`), DNS resolution, and the one-at-a-time connect latch that `ConnectReady` releases. |
| Patches | `SkipIntroPatches.cs`, `AuthFallbackPatches.cs`, `LocalHostWorldLoadPatches.cs`, `SpawnStateHeartbeat.cs`, `LoadStateProbe.cs`, `AliveFlagsTrace.cs`, `SpawnedTrace.cs`, `WindowTrace.cs`, `PerfTrace.cs` | Harmony patches, one file per game area. The last four are diagnostics, gated behind `DiagToggle`. |
| Commands and entry | `ConsoleCmdConnect.cs`, `ConsoleCmdDiag.cs`, `ModApi.cs` | F1 commands and the mod entry point. |

Two conventions worth knowing before adding a file:

- A log line is written as `Log.Out(LogText.Tag + "...")`. The prefix is
  `LogText.Tag`, one constant, because `one_shot_join.sh` greps the client log
  for it.
- A probe that runs per frame announces its own failures through
  `ProbeFailure.Once`, so a renamed game field cannot flood the log.

## scripts

Four groups, by what calls what.

- Libraries, sourced by the launchers and by the gates that test them:
  `config_validate.sh`, `join_evidence.sh`, `log_markers.sh`, `log_sanitize.sh`,
  `monotonic_clock.sh`, `proton_paths.sh`, `audio_streams.sh`.
- Entry points a person or a runner invokes: `launch_client.sh`,
  `restart_pair.sh`, `one_shot_join.sh`, `zero_nre_join_loop.sh`,
  `mute_client_audio.sh`, `unmute_client_audio.sh`.
- Build and release support the Makefile drives: `check_prereqs.sh`,
  `check_game_root.sh`, `assert_tool_pin.sh`, `changelog_gate.sh`,
  `package.sh`, `stage_mod.sh`, `repro_zip.sh`, `coverage-cs.sh`, plus
  `coverage_badge.py`.
- Gates: every `test_*.sh` and the Python gate
  `test_launch_client_platform.py`, with their helpers `test_common.sh`,
  `harness_csproj.sh`, `conftest.py` and `testdata/`.

The rule that keeps this readable: a gate sources libraries and
`test_common.sh`, never another gate, and a library never sources a gate.
