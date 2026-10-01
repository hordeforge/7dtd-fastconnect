using System;
using HarmonyLib;

namespace SdtdConnect
{
    /// <summary>
    /// Client-only join helper for local/dev servers (7dtd dedicated, zdtd).
    /// Auto-joins once the main menu opens when 7DTD_CONNECT / -connect= is
    /// set; the F1 `connect` command is registered in every mode.
    /// Does not invent world/chunk/sign/spawn state for missing server packages.
    /// </summary>
    public class ModApi : IModApi
    {
        public const string HarmonyId = "com.7dtd.connect";
        public const string Version = "0.13.0";
        public const string PlayerNameEnv = "7DTD_PLAYER_NAME";
        static bool _autoTried;

        public void InitMod(Mod _modInstance)
        {
            DiagToggle.AnnounceOnce();
            Log.Out(LogText.Tag + "InitMod v" + Version + " (connect/join only; playtest is 7dtd-playtest); diag " + (DiagToggle.Enabled ? "ON" : "OFF") + " (`diag on/off/status`, or 7DTD_CONNECT_DEBUG=1)");
            Log.Out(LogText.Tag + "automation boot mode " + (AutomationMode.Enabled ? "enabled" : "disabled")
                + " (auto when 7DTD_CONNECT/-connect is present; override with " + AutomationMode.EnvVar + ")");

            TryStep("intro movie disable", () =>
            {
                // This is a user-facing preference, not automation plumbing.
                GamePrefs.Set(EnumGamePrefs.OptionsIntroMovieEnabled, false);
                if (GameManager.Instance != null)
                    GameManager.Instance.showOpenerMovieOnLoad = false;
                GamePrefs.Instance?.Save();
            });

            if (AutomationMode.Enabled)
            {
                // Separate steps from each other: the uncap and the force-sync
                // setting are independent settings, and one warning covering
                // both left the reader unable to tell which of them did not
                // apply. Same type-and-message shape as every other step here.
                TryStep("frame uncap", () =>
                {
                    // Stock only enables RIB in editor; async addressables starve at ~1 FPS under Proton.
                    BootUnblock.ApplyFrameUncap("InitMod");
                });

                TryStep("forceLoadSync set", () => BootUnblock.ApplyForceLoadSync());

                TryStep("Discord prefs set", () =>
                {
                    GamePrefs.Set(EnumGamePrefs.DiscordDisabled, true);
                    GamePrefs.Set(EnumGamePrefs.DiscordFirstTimeInfoShown, true);
                });

                // Separate step: an EULA accept failure blocks startup, and a
                // message blaming Discord sends the reader after the wrong
                // setting. EULA gate blocks MainMenu (scroll+accept); force
                // accepted for automation.
                TryStep("EULA prefs accept", () =>
                    Log.Out(LogText.Tag + "EULA prefs accepted=" + EulaSkip.AcceptLatest()));
            }

            // 7DTD_PLAYER_NAME is an operator ask, not automation plumbing, so
            // it is honoured in every mode: gating it on automation mode made
            // the variable silently do nothing for a client launched without a
            // join target, which is exactly the Local-platform launch README
            // documents it for. The no-env fallback (store a name the stock
            // dedi would otherwise reject) stays automation-only, so an
            // ordinary client launch still leaves the stored pref alone.
            TryStep("player name override", ApplyPlayerNameOverride);

            try
            {
                var harmony = new Harmony(HarmonyId);
                int ok = 0, fail = 0;
                foreach (var t in typeof(ModApi).Assembly.GetTypes())
                {
                    if (t.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0)
                        continue;
                    if (!AutomationMode.Enabled
                        && t.GetCustomAttributes(typeof(AutomationPatchAttribute), true).Length != 0)
                        continue;
                    try
                    {
                        harmony.CreateClassProcessor(t).Patch();
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Log.Warning(LogText.Tag + "Harmony skip " + t.Name + ": " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
                // Error severity when a patch failed: the mod then runs
                // half-patched (a menu forced open behind a gate that was
                // never skipped), and an info line in a log the harness greps
                // hides that.
                string summary = LogText.Tag + "Harmony patches applied ok=" + ok + " fail=" + fail
                    + " (news/discord skip for automation only)";
                if (fail > 0) Log.Error(summary); else Log.Out(summary);
            }
            catch (Exception ex)
            {
                Log.Error(LogText.Tag + "Harmony failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (AutomationMode.Enabled)
            {
                TryStep("InitMod news-screen skip", () => XUiC_MainMenu.shownNewsScreenOnce = true);
            }

            // Error severity, not the TryStep warning: without the handler the
            // auto-join never runs, and the log must not read as a normal boot.
            try
            {
                ModEvents.MainMenuOpened.RegisterHandler(OnMainMenuOpened);
            }
            catch (Exception ex)
            {
                Log.Error(LogText.Tag + "MainMenuOpened register failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Runs one optional init step and reports a throw as a warning instead
        /// of letting it stop the rest of InitMod. Every optional-pref and
        /// skip step goes through here, so each one costs only its own setting
        /// and the reported shape (what failed, exception type, message) is
        /// the same for all of them.
        /// </summary>
        static void TryStep(string what, Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                Log.Warning(LogText.Tag + what + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Picks the value stored in the stock PlayerName pref:
        /// 7DTD_PLAYER_NAME when it normalizes to something non-empty, else
        /// (automation only) PlayerNames.Resolve() when the stored pref is
        /// empty. Outside automation a pref that already holds a name is left
        /// alone and an unset env writes nothing, so an ordinary client launch
        /// still leaves the stored identity alone. The server still
        /// authenticates and persists whatever is stored; this only chooses
        /// which identity the client presents.
        /// </summary>
        static void ApplyPlayerNameOverride()
        {
            string requested = EnvFlags.Read(PlayerNameEnv);
            bool fromEnv = !string.IsNullOrWhiteSpace(requested);
            if (!fromEnv)
            {
                if (!AutomationMode.Enabled) return;
                // Stock dedi kicks "Empty name or player ID" for loopback joins when Steam is offline,
                // so store a non-empty PlayerName even without the env set.
                // A prefs read that throws leaves the stored name unknown, not
                // empty: overwriting it would destroy the player's identity on
                // a transient store failure, so the read failing skips the
                // write and is logged.
                try
                {
                    string existing = GamePrefs.GetString(EnumGamePrefs.PlayerName);
                    if (!string.IsNullOrWhiteSpace(existing)) return;
                }
                catch (Exception ex)
                {
                    Log.Warning(LogText.Tag + "stored PlayerName read failed; leaving it untouched: "
                        + ex.GetType().Name + ": " + ex.Message);
                    return;
                }
                requested = PlayerNames.Resolve();
            }
            else
            {
                // A value made only of characters normalization strips (a lone
                // bidi override, say) normalizes to nothing, and the server
                // kicks an empty name. Fall back to the same identity the unset
                // path uses rather than storing an empty pref.
                requested = PlayerNames.Normalize(requested);
                if (string.IsNullOrEmpty(requested)) requested = PlayerNames.Resolve();
            }
            // A throwing write is reported by the caller's TryStep under the
            // same name, so it is not guarded again here.
            GamePrefs.Set(EnumGamePrefs.PlayerName, requested);
            GamePrefs.Instance?.Save();
            // The applied name is the player's own identity (OS account
            // name, host name, or an operator-supplied label) and the
            // client log is what gets pasted into bug reports, so the
            // value never reaches it: only the source is recorded.
            // Name the real source, since a fallback logged as "from
            // 7DTD_PLAYER_NAME" would send someone debugging after an env
            // value that is not set.
            Log.Out(fromEnv
                ? LogText.Tag + "player name applied from " + PlayerNameEnv
                : LogText.Tag + "player name applied from fallback ("
                    + PlayerNameEnv + " unset, stored PlayerName empty)");
        }

        static void OnMainMenuOpened(ref ModEvents.SMainMenuOpenedData _data)
        {
            DiagToggle.AnnounceOnce();
            if (AutomationMode.Enabled)
            {
                TryStep("MainMenuOpened news/intro skip", () =>
                {
                    XUiC_MainMenu.shownNewsScreenOnce = true;
                    if (GameManager.Instance != null)
                        GameManager.Instance.showOpenerMovieOnLoad = false;
                });
            }

            if (_autoTried) return;

            // Latched after the launch context resolves, not on entry: the
            // latch is the one auto-join attempt for the session, and a
            // resolution that threw (env read or logger blocked) had not spent
            // it. Left armed on a throw, the next main-menu open returns
            // immediately and the client never auto-joins, with nothing in the
            // log but the probe's first-failure notice.
            string host;
            int port;
            string source;
            bool haveTarget;
            try
            {
                haveTarget = ConnectTarget.TryFromLaunchContext(out host, out port, out source);
            }
            catch (Exception ex)
            {
                ProbeFailure.Once("auto-join target", ex);
                return;
            }
            _autoTried = true;

            if (!haveTarget)
            {
                // "no usable" covers both unset and set-but-rejected: the
                // rejection already warned with its own reason, and claiming
                // "no 7DTD_CONNECT" after it would send someone debugging
                // after why their variable was not seen at all.
                Log.Out(LogText.Tag + "auto-join idle (no usable 7DTD_CONNECT / -connect=); use F1: connect 127.0.0.1 27025");
                return;
            }

            Log.Out(LogText.Tag + "auto-join from " + source);
            // DoSpawn opens XUiC_SpawnSelectionWindow unless SkipSpawnButton is
            // true; auto-connect needs the direct RequestToSpawn path (no UI
            // click). Set here rather than inside ConnectTarget.TryConnect so
            // the connect plumbing stays independent of AutomationMode, and F1
            // joins keep stock behaviour: the pref persists, so setting it
            // outside automation would suppress the spawn window in ordinary
            // play too.
            if (AutomationMode.Enabled)
                TryStep("SkipSpawnButton set", () => GamePrefs.Set(EnumGamePrefs.SkipSpawnButton, true));

            try
            {
                ThreadManager.StartCoroutine(DelayedConnect(host, port));
            }
            catch (Exception ex)
            {
                Log.Warning(LogText.Tag + "coroutine failed, connecting immediately: " + ex.GetType().Name + ": " + ex.Message);
                ConnectAndLog(host, port);
            }
        }

        static System.Collections.IEnumerator DelayedConnect(string host, int port)
        {
            // SetupProtocols NREs on PlatformManager.NativePlatform before EOS/Steam settle.
            // Force-open CheckLogin fires MainMenuOpened ~1s before [EOS] Login succeeded;
            // the connect-ready gate waits for the cross (EOS) user. Cap by monotonic
            // time, not frames, because uncapped boot ticks thousands of frames per
            // second (a frame cap would expire long before the EOS settle windows in ConnectReady).
            // realtimeSinceStartup, not unscaledTime: the budget is real seconds,
            // and unscaledTime is accumulated from unscaledDeltaTime, which Unity
            // clamps to Time.maximumDeltaTime (0.333s). A boot that stalls for a
            // second a frame would let this 45s cap run for minutes of wall clock,
            // which is exactly the boot this gate exists to bound.
            // Poll on a monotonic interval, not per frame: IsReady touches several
            // subsystems and would otherwise run thousands of times per second
            // under the uncapped boot; 10 Hz costs at most 100 ms of extra
            // join latency against multi-second settle windows.
            const float maxWaitSec = 45f;
            const float pollIntervalSec = 0.1f;
            // Progress cadence for the wait, independent of the poll rate:
            // one line per poll would be 450 lines for a single timeout.
            const float waitLogIntervalSec = 5f;
            float waitStart = UnityEngine.Time.realtimeSinceStartup;
            float nextLog = 0f;
            int polls = 0;
            bool ready = ConnectReady.IsReady(out string whyNot);
            while (!ready && UnityEngine.Time.realtimeSinceStartup - waitStart < maxWaitSec)
            {
                if (polls == 0 || UnityEngine.Time.realtimeSinceStartup >= nextLog)
                {
                    nextLog = UnityEngine.Time.realtimeSinceStartup + waitLogIntervalSec;
                    Log.Out(LogText.Tag + "connect wait t="
                        + ElapsedSec(waitStart) + "s polls=" + polls + " " + whyNot);
                }
                polls++;
                // Fresh waiter per poll: WaitForSecondsRealtime reset semantics
                // vary across Unity versions, and a fresh instance degrades to a
                // plain per-frame yield if Reset is not invoked.
                yield return new UnityEngine.WaitForSecondsRealtime(pollIntervalSec);
                ready = ConnectReady.IsReady(out whyNot);
            }

            // Elapsed seconds on every wait line: a join that hangs is read from
            // how long the gate held before it connected or gave up, and polls
            // alone only say how often, not for how long.
            if (!ready)
                Log.Warning(LogText.Tag + "connect gate timeout t=" + ElapsedSec(waitStart)
                    + "s polls=" + polls + " " + whyNot + "; trying anyway");
            else if (polls > 0)
                Log.Out(LogText.Tag + "connect-ready t=" + ElapsedSec(waitStart)
                    + "s polls=" + polls);

            ConnectAndLog(host, port);
        }

        // Seconds on the mod's monotonic clock (realtimeSinceStartup, the same
        // one the deadlines use), one decimal: enough to place a join in a boot
        // timeline, short enough to stay on one log line.
        // Invariant culture so a comma-decimal locale cannot write "3,5s",
        // which reads as part of a list in a log line.
        static string ElapsedSec(float start)
        {
            return (UnityEngine.Time.realtimeSinceStartup - start)
                .ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        static void ConnectAndLog(string host, int port)
        {
            if (!ConnectTarget.TryConnect(host, port, out string msg))
                Log.Error(LogText.Tag + msg);
            else
                Log.Out(LogText.Tag + msg);
        }
    }
}
