using System;
using HarmonyLib;

namespace ZdtdConnect
{
    /// <summary>
    /// Client-only join helper for local/dev servers (zdtd).
    /// Auto-join when ZDTD_CONNECT / -connect= is set; F1 connect command otherwise.
    /// Does not invent world/chunk/sign/spawn state for missing server packages.
    /// </summary>
    public class ModApi : IModApi
    {
        public const string HarmonyId = "com.zdtd.connect";
        public const string Version = "0.9.5";
        public const string PlayerNameEnv = "ZDTD_PLAYER_NAME";
        static bool _autoTried;
        static Harmony _harmony;

        public void InitMod(Mod _modInstance)
        {
            DiagToggle.AnnounceOnce();
            Log.Out("[zdtd-connect] InitMod v" + Version + " (connect/join only; playtest is 7dtd-playtest) — diag " + (DiagToggle.Enabled ? "ON" : "OFF") + " (`diag on/off/status`, or ZDTD_CONNECT_DEBUG=1)");

            try
            {
                // Stock only enables RIB in editor; async addressables starve at ~1 FPS under Proton.
                BootUnblock.ApplyFrameUncap("InitMod");
                BootUnblock.ApplyForceLoadSync();
            }
            catch (Exception ex)
            {
                Log.Warning("[zdtd-connect] boot unblock failed: " + ex.Message);
            }

            try
            {
                ApplyPlayerNameOverride();
                try { ZdtdConnect.Patch_ClientInfo_PlayerName_Guard_FieldFallback.EnsurePrefsName(); } catch { }
                GamePrefs.Set(EnumGamePrefs.DiscordDisabled, true);
                GamePrefs.Set(EnumGamePrefs.DiscordFirstTimeInfoShown, true);
                GamePrefs.Set(EnumGamePrefs.OptionsIntroMovieEnabled, false);
                // EULA gate blocks MainMenu (scroll+accept); force accepted for automation.
                int latest = GamePrefs.GetInt(EnumGamePrefs.EulaLatestVersion);
                if (latest < 1) latest = 99;
                GamePrefs.Set(EnumGamePrefs.EulaLatestVersion, latest);
                GamePrefs.Set(EnumGamePrefs.EulaVersionAccepted, latest);
                GamePrefs.Instance?.Save();
                Log.Out("[zdtd-connect] EULA prefs accepted=" + latest);
            }
            catch (Exception ex)
            {
                Log.Warning("[zdtd-connect] Discord/intro/eula prefs set failed: " + ex.Message);
            }

            try
            {
                _harmony = new Harmony(HarmonyId);
                int ok = 0, fail = 0;
                foreach (var t in typeof(ModApi).Assembly.GetTypes())
                {
                    if (t.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0)
                        continue;
                    try
                    {
                        _harmony.CreateClassProcessor(t).Patch();
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Log.Warning("[zdtd-connect] Harmony skip " + t.Name + ": " + ex.Message);
                    }
                }
                Log.Out("[zdtd-connect] Harmony patches applied ok=" + ok + " fail=" + fail
                    + " (news/discord skip for automation only)");
            }
            catch (Exception ex)
            {
                Log.Error("[zdtd-connect] Harmony failed: " + ex.Message);
            }

            try
            {
                XUiC_MainMenu.shownNewsScreenOnce = true;
            }
            catch { /* type may not be ready */ }

            try
            {
                ModEvents.MainMenuOpened.RegisterHandler(OnMainMenuOpened);
            }
            catch (Exception ex)
            {
                Log.Error("[zdtd-connect] MainMenuOpened register failed: " + ex.Message);
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
            if (string.IsNullOrWhiteSpace(requested))
            {
                // Stock dedi kicks "Empty name or player ID" for loopback joins when Steam is offline.
                // Ensure ClientInfo.playerName is never empty even without env.
                try
                {
                    string existing = GamePrefs.GetString(EnumGamePrefs.PlayerName);
                    if (!string.IsNullOrWhiteSpace(existing)) return;
                }
                catch { }
                requested = Environment.UserName;
                if (string.IsNullOrWhiteSpace(requested)) requested = "maci";
                requested = requested.Trim();
                if (requested.Length > 24) requested = requested.Substring(0, 24);
            }
            else requested = requested.Trim();
            try
            {
                GamePrefs.Set(EnumGamePrefs.PlayerName, requested);
                GamePrefs.Instance?.Save();
                Log.Out("[zdtd-connect] player name from " + PlayerNameEnv + "=" + requested);
            }
            catch (Exception ex)
            {
                Log.Warning("[zdtd-connect] player name override failed: " + ex.Message);
            }
        }

        static void OnMainMenuOpened(ref ModEvents.SMainMenuOpenedData _data)
        {
            DiagToggle.AnnounceOnce();
            try
            {
                XUiC_MainMenu.shownNewsScreenOnce = true;
                if (GameManager.Instance != null)
                    GameManager.Instance.showOpenerMovieOnLoad = false;
            }
            catch { /* ignore */ }

            if (_autoTried) return;
            _autoTried = true;

            if (!ConnectTarget.TryFromLaunchContext(out string host, out int port, out string source))
            {
                Log.Out("[zdtd-connect] no ZDTD_CONNECT / -connect= ; use F1: connect 127.0.0.1 27025");
                return;
            }

            Log.Out("[zdtd-connect] auto-join from " + source);
            try
            {
                ThreadManager.StartCoroutine(DelayedConnect(host, port));
            }
            catch (Exception ex)
            {
                Log.Warning("[zdtd-connect] coroutine failed, connecting immediately: " + ex.Message);
                if (!ConnectTarget.TryConnect(host, port, out string msg))
                    Log.Error("[zdtd-connect] " + msg);
                else
                    Log.Out("[zdtd-connect] " + msg);
            }
        }

        static System.Collections.IEnumerator DelayedConnect(string host, int port)
        {
            // SetupProtocols NREs on PlatformManager.NativePlatform before EOS/Steam settle.
            // Force-open CheckLogin fires MainMenuOpened ~1s before [EOS] Login succeeded.
            const int maxFrames = 3600; // ~60s at 60fps; uncapped boot is much faster
            int waited = 0;
            while (waited < maxFrames)
            {
                if (ConnectReady.IsReady(out string whyNot))
                {
                    if (waited > 0)
                        Log.Out("[zdtd-connect] connect-ready after frames=" + waited);
                    break;
                }
                if (waited == 0 || waited % 300 == 0)
                    Log.Out("[zdtd-connect] connect wait frames=" + waited + " " + whyNot);
                waited++;
                yield return null;
            }

            if (!ConnectReady.IsReady(out string still))
            {
                Log.Warning("[zdtd-connect] connect gate timeout frames=" + waited + " " + still + "; trying anyway");
            }

            if (!ConnectTarget.TryConnect(host, port, out string msg))
                Log.Error("[zdtd-connect] " + msg);
            else
                Log.Out("[zdtd-connect] " + msg);
        }
    }
}
