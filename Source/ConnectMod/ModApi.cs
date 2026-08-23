using System;
using HarmonyLib;

namespace SdtdConnect
{
    /// <summary>
    /// Client-only join helper for local/dev servers (7dtd dedicated, zdtd).
    /// Auto-join when 7DTD_CONNECT / -connect= is set; F1 connect command otherwise.
    /// Does not invent world/chunk/sign/spawn state for missing server packages.
    /// </summary>
    public class ModApi : IModApi
    {
        public const string HarmonyId = "com.7dtd.connect";
        public const string Version = "0.10.4";
        public const string PlayerNameEnv = "7DTD_PLAYER_NAME";
        static bool _autoTried;

        public void InitMod(Mod _modInstance)
        {
            DiagToggle.AnnounceOnce();
            Log.Out("[7dtd-fastconnect] InitMod v" + Version + " (connect/join only; playtest is 7dtd-playtest) — diag " + (DiagToggle.Enabled ? "ON" : "OFF") + " (`diag on/off/status`, or 7DTD_CONNECT_DEBUG=1)");
            Log.Out("[7dtd-fastconnect] automation boot mode " + (AutomationMode.Enabled ? "enabled" : "disabled")
                + " (auto when 7DTD_CONNECT/-connect is present; override with " + AutomationMode.EnvVar + ")");

            if (AutomationMode.Enabled) try
            {
                // Stock only enables RIB in editor; async addressables starve at ~1 FPS under Proton.
                BootUnblock.ApplyFrameUncap("InitMod");
                BootUnblock.ApplyForceLoadSync();
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] boot unblock failed: " + ex.Message);
            }

            if (AutomationMode.Enabled) try
            {
                ApplyPlayerNameOverride();
                GamePrefs.Set(EnumGamePrefs.DiscordDisabled, true);
                GamePrefs.Set(EnumGamePrefs.DiscordFirstTimeInfoShown, true);
                GamePrefs.Set(EnumGamePrefs.OptionsIntroMovieEnabled, false);
                // EULA gate blocks MainMenu (scroll+accept); force accepted for automation.
                Log.Out("[7dtd-fastconnect] EULA prefs accepted=" + EulaSkip.AcceptLatest());
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] Discord/intro/eula prefs set failed: " + ex.Message);
            }

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
                        Log.Warning("[7dtd-fastconnect] Harmony skip " + t.Name + ": " + ex.Message);
                    }
                }
                Log.Out("[7dtd-fastconnect] Harmony patches applied ok=" + ok + " fail=" + fail
                    + " (news/discord skip for automation only)");
            }
            catch (Exception ex)
            {
                Log.Error("[7dtd-fastconnect] Harmony failed: " + ex.Message);
            }

            if (AutomationMode.Enabled) try
            {
                XUiC_MainMenu.shownNewsScreenOnce = true;
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] InitMod news-screen skip failed: " + ex.Message);
            }

            try
            {
                ModEvents.MainMenuOpened.RegisterHandler(OnMainMenuOpened);
            }
            catch (Exception ex)
            {
                Log.Error("[7dtd-fastconnect] MainMenuOpened register failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Selects a real stock Local-platform identity before auto-join.
        /// The server still authenticates and persists this identity normally;
        /// this only selects the stock player's configured display/name key.
        /// </summary>
        static void ApplyPlayerNameOverride()
        {
            string requested = Environment.GetEnvironmentVariable(PlayerNameEnv);
            bool fromEnv = !string.IsNullOrWhiteSpace(requested);
            if (!fromEnv)
            {
                // Stock dedi kicks "Empty name or player ID" for loopback joins when Steam is offline.
                // Ensure ClientInfo.playerName is never empty even without env.
                try
                {
                    string existing = GamePrefs.GetString(EnumGamePrefs.PlayerName);
                    if (!string.IsNullOrWhiteSpace(existing)) return;
                }
                catch { }
                requested = PlayerNames.Resolve();
            }
            else
            {
                requested = requested.Trim();
                if (requested.Length > PlayerNames.MaxLength)
                    requested = requested.Substring(0, PlayerNames.MaxLength);
            }
            try
            {
                GamePrefs.Set(EnumGamePrefs.PlayerName, requested);
                GamePrefs.Instance?.Save();
                // Name the real source: a fallback logged as "from 7DTD_PLAYER_NAME="
                // would send someone debugging after an env value that is not set.
                Log.Out(fromEnv
                    ? "[7dtd-fastconnect] player name from " + PlayerNameEnv + "=" + requested
                    : "[7dtd-fastconnect] player name fallback '" + requested
                        + "' (" + PlayerNameEnv + " unset, stored PlayerName empty)");
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] player name override failed: " + ex.Message);
            }
        }

        static void OnMainMenuOpened(ref ModEvents.SMainMenuOpenedData _data)
        {
            DiagToggle.AnnounceOnce();
            if (AutomationMode.Enabled) try
            {
                XUiC_MainMenu.shownNewsScreenOnce = true;
                if (GameManager.Instance != null)
                    GameManager.Instance.showOpenerMovieOnLoad = false;
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] MainMenuOpened news/intro skip failed: " + ex.Message);
            }

            if (_autoTried) return;
            _autoTried = true;

            if (!ConnectTarget.TryFromLaunchContext(out string host, out int port, out string source))
            {
                Log.Out("[7dtd-fastconnect] no 7DTD_CONNECT / -connect= ; use F1: connect 127.0.0.1 27025");
                return;
            }

            Log.Out("[7dtd-fastconnect] auto-join from " + source);
            try
            {
                ThreadManager.StartCoroutine(DelayedConnect(host, port));
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] coroutine failed, connecting immediately: " + ex.Message);
                ConnectAndLog(host, port);
            }
        }

        static System.Collections.IEnumerator DelayedConnect(string host, int port)
        {
            // SetupProtocols NREs on PlatformManager.NativePlatform before EOS/Steam settle.
            // Force-open CheckLogin fires MainMenuOpened ~1s before [EOS] Login succeeded;
            // the connect-ready gate waits for the cross (EOS) user. Cap by wall time,
            // not frames, because uncapped boot ticks thousands of frames per second
            // (a frame cap would expire long before the EOS settle windows in ConnectReady).
            // Poll on a wall interval, not per frame: IsReady touches several
            // subsystems and would otherwise run thousands of times per second
            // under the uncapped boot; 10 Hz costs at most 100 ms of extra
            // join latency against multi-second settle windows.
            const float maxWaitSec = 45f;
            const float pollIntervalSec = 0.1f;
            float waitStart = UnityEngine.Time.unscaledTime;
            float nextLog = 0f;
            int polls = 0;
            while (UnityEngine.Time.unscaledTime - waitStart < maxWaitSec)
            {
                if (ConnectReady.IsReady(out string whyNot))
                {
                    if (polls > 0)
                        Log.Out("[7dtd-fastconnect] connect-ready after polls=" + polls);
                    break;
                }
                if (polls == 0 || UnityEngine.Time.unscaledTime >= nextLog)
                {
                    nextLog = UnityEngine.Time.unscaledTime + 5f;
                    Log.Out("[7dtd-fastconnect] connect wait polls=" + polls + " " + whyNot);
                }
                polls++;
                // Fresh waiter per poll: WaitForSecondsRealtime reset semantics
                // vary across Unity versions, and a fresh instance degrades to a
                // plain per-frame yield if Reset is not invoked.
                yield return new UnityEngine.WaitForSecondsRealtime(pollIntervalSec);
            }

            if (!ConnectReady.IsReady(out string still))
            {
                Log.Warning("[7dtd-fastconnect] connect gate timeout polls=" + polls + " " + still + "; trying anyway");
            }

            ConnectAndLog(host, port);
        }

        static void ConnectAndLog(string host, int port)
        {
            if (!ConnectTarget.TryConnect(host, port, out string msg))
                Log.Error("[7dtd-fastconnect] " + msg);
            else
                Log.Out("[7dtd-fastconnect] " + msg);
        }
    }
}
