using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SdtdConnect
{
    /// <summary>
    /// Proton/headless: keep main thread + addressables moving.
    /// Stock RIB only in editor; VSync/FPS cap + async addressables stall at ~1 FPS.
    /// forceLoadSync makes LoadManager WaitForCompletion (same as dedi path).
    /// </summary>
    static class BootUnblock
    {
        internal const string ForceLoadSyncEnv = "7DTD_CONNECT_FORCE_LOAD_SYNC";

        static bool _forceSyncSet;
        static bool _forceSyncOptOutLogged;
        // Snapshot once: hooks call this every frame and the process env
        // cannot change at runtime.
        static bool? _forceSyncEnabled;

        internal static bool ForceLoadSyncEnabled()
        {
            if (_forceSyncEnabled.HasValue) return _forceSyncEnabled.Value;
            string value = null;
            try { value = Environment.GetEnvironmentVariable(ForceLoadSyncEnv); }
            catch { }
            _forceSyncEnabled = string.IsNullOrWhiteSpace(value) || EnvFlags.IsSetOn(value);
            return _forceSyncEnabled.Value;
        }

        internal static void ApplyFrameUncap(string reason)
        {
            try
            {
                // Hooks call this every frame; stock re-caps between calls, so
                // write only what changed instead of all four engine properties.
                if (!Application.runInBackground) Application.runInBackground = true;
                if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
                if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
                if (Application.backgroundLoadingPriority != ThreadPriority.High)
                    Application.backgroundLoadingPriority = ThreadPriority.High;
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-connect] frame uncap failed (" + reason + "): " + ex.Message);
            }
        }

        internal static void ApplyForceLoadSync()
        {
            if (_forceSyncSet) return;
            if (!ForceLoadSyncEnabled())
            {
                if (!_forceSyncOptOutLogged)
                {
                    _forceSyncOptOutLogged = true;
                    Log.Out("[7dtd-connect] LoadManager.forceLoadSync disabled by "
                        + ForceLoadSyncEnv);
                }
                return;
            }
            try
            {
                var fi = typeof(LoadManager).GetField("forceLoadSync",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi == null || fi.FieldType != typeof(bool))
                {
                    Log.Warning("[7dtd-connect] LoadManager.forceLoadSync field missing");
                    return;
                }
                fi.SetValue(null, true);
                _forceSyncSet = true;
                Log.Out("[7dtd-connect] LoadManager.forceLoadSync=true (automation addressables)");
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-connect] forceLoadSync set failed: " + ex.Message);
            }
        }
    }

    /// <summary>EULA acceptance shared by InitMod prefs and every skip patch.</summary>
    internal static class EulaSkip
    {
        /// <summary>Marks the latest EULA accepted and persists it. Returns the recorded version.</summary>
        internal static int AcceptLatest()
        {
            int latest = GamePrefs.GetInt(EnumGamePrefs.EulaLatestVersion);
            if (latest < 1) latest = 99;
            GamePrefs.Set(EnumGamePrefs.EulaLatestVersion, latest);
            GamePrefs.Set(EnumGamePrefs.EulaVersionAccepted, latest);
            GamePrefs.Instance?.Save();
            return latest;
        }

        /// <summary>
        /// Shared body for both GUIWindowManager.Open arities that can open
        /// "windowEula": accept, reopen the main menu, and re-fire
        /// MainMenuOpened directly so auto-join fires even if the XUi path is gated.
        /// Once the window is windowEula it never falls back to stock Open.
        /// </summary>
        internal static bool BlockGateWindow(GUIWindowManager wm, string _windowName, string logTag)
        {
            if (_windowName != "windowEula") return true;
            try
            {
                Log.Out("[7dtd-connect] blocking GUI " + logTag);
                AcceptLatest();
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-connect] windowEula accept failed (" + logTag + "): " + ex.Message);
            }
            try
            {
                var xui = wm?.playerUI?.xui;
                if (xui != null) XUiC_MainMenu.Open(xui);
                var data = new ModEvents.SMainMenuOpenedData(true);
                ModEvents.MainMenuOpened.Invoke(ref data);
                Log.Out("[7dtd-connect] dispatched MainMenuOpened after " + logTag);
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-connect] MainMenuOpened dispatch failed (" + logTag + "): " + ex.Message);
            }
            return false;
        }
    }

    [AutomationPatch]
    [HarmonyPatch(typeof(GameManager), "Awake")]
    static class Patch_GameManager_Awake_RunInBackground
    {
        static void Postfix()
        {
            BootUnblock.ApplyFrameUncap("Awake");
            BootUnblock.ApplyForceLoadSync();
            Log.Out("[7dtd-connect] boot unblock RIB+noVSync+uncappedFPS");
        }
    }

    /// <summary>Stock UpdateFPSCap re-applies VSync refresh cap before GameHasStarted; keep uncapped.</summary>
    [AutomationPatch]
    [HarmonyPatch(typeof(GameManager), "UpdateFPSCap")]
    static class Patch_GameManager_UpdateFPSCap
    {
        static void Postfix()
        {
            BootUnblock.ApplyFrameUncap("UpdateFPSCap");
        }
    }

    /// <summary>
    /// Skip news "click to continue" by treating it as already shown.
    /// Intro splash video is skipped via -skipintro on the process argv (before mods load).
    /// </summary>
    [AutomationPatch]
    [HarmonyPatch(typeof(XUiC_MainMenu), nameof(XUiC_MainMenu.Open), new Type[] { typeof(XUi) })]
    static class Patch_MainMenu_Open
    {
        static void Prefix()
        {
            // A throwing prefix propagates into stock Open() and kills the menu,
            // so guard like the other skip patches.
            try
            {
                // Stock Open() opens NewsScreen when shownNewsScreenOnce is false.
                XUiC_MainMenu.shownNewsScreenOnce = true;
                BootUnblock.ApplyFrameUncap("MainMenu.Open");
                if (GameManager.Instance != null)
                    GameManager.Instance.showOpenerMovieOnLoad = false;
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-connect] MainMenu.Open prefix failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Boot progress heartbeat so join harness can see static-load stalls.
    /// </summary>
    [AutomationPatch]
    [HarmonyPatch(typeof(MainMenuMono), "Update")]
    static class Patch_MainMenuMono_Update_Heartbeat
    {
        static float _nextLog;
        static int _ticks;

        static void Prefix(MainMenuMono __instance)
        {
            _ticks++;
            BootUnblock.ApplyFrameUncap("hb");
            if (Time.unscaledTime < _nextLog) return;
            _nextLog = Time.unscaledTime + 5f;
            try
            {
                var gm = GameManager.Instance;
                bool loaded = gm != null && gm.bStaticDataLoaded;
                string action = gm != null ? gm.CurrentLoadAction : "?";
                Log.Out("[7dtd-connect] boot hb ticks=" + _ticks
                    + " focused=" + Application.isFocused
                    + " rib=" + Application.runInBackground
                    + " vsync=" + QualitySettings.vSyncCount
                    + " tfr=" + Application.targetFrameRate
                    + " static=" + loaded
                    + " loginDone=" + (__instance != null && __instance.loginCheckDone)
                    + " openMM=" + (__instance != null && __instance.bOpenMainMenu)
                    + " action=" + action);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Steam Login can stall under Proton when unfocused. After static data is ready,
    /// force main-menu open so auto-join runs (test harness only).
    /// </summary>
    [AutomationPatch]
    [HarmonyPatch(typeof(MainMenuMono), "CheckLogin")]
    static class Patch_MainMenuMono_CheckLogin
    {
        static bool Prefix(MainMenuMono __instance, ref bool __result)
        {
            try
            {
                // Still start stock login so Steam/EOS identity can settle in background.
                // But do not block menu open on the callback.
                __instance.loginCheckDone = true;
                __instance.bOpenMainMenu = true;
                __result = true;
                Log.Out("[7dtd-connect] CheckLogin force-open main menu (automation)");
                try
                {
                    var xui = __instance.windowManager?.playerUI?.xui;
                    if (xui != null)
                        XUiC_LoginBase.Login(xui, () => { /* stock OnLoginComplete path optional */ });
                }
                catch (Exception ex)
                {
                    Log.Warning("[7dtd-connect] background Login start failed: " + ex.Message);
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-connect] CheckLogin patch failed: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>If news is already open (race / earlier open path), close it and open main menu.</summary>
    [AutomationPatch]
    [HarmonyPatch(typeof(XUiC_NewsScreen), nameof(XUiC_NewsScreen.Open), new Type[] { typeof(XUi) })]
    static class Patch_NewsScreen_Open
    {
        // Stock signature uses _xuiInstance (not _xui); Harmony matches by name.
        static bool Prefix(XUi _xuiInstance)
        {
            try
            {
                Log.Out("[7dtd-connect] blocking news screen open");
                XUiC_MainMenu.shownNewsScreenOnce = true;
                // Fall through to main menu instead of news.
                if (_xuiInstance != null)
                    XUiC_MainMenu.Open(_xuiInstance);
                return false; // skip original NewsScreen.Open
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-connect] news skip failed: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>Never initialize Discord SDK / login (RPC connect spam and login UI).</summary>
    [AutomationPatch]
    [HarmonyPatch(typeof(DiscordManager), nameof(DiscordManager.Init))]
    static class Patch_DiscordManager_Init
    {
        static bool Prefix()
        {
            Log.Out("[7dtd-connect] skipping DiscordManager.Init");
            return false;
        }
    }

    /// <summary>Do not open first-time Discord info / login window on main menu.</summary>
    [AutomationPatch]
    [HarmonyPatch(typeof(DiscordManager), "mainMenuOpening")]
    static class Patch_DiscordManager_mainMenuOpening
    {
        static bool Prefix(ref ModEvents.EModEventResult __result)
        {
            __result = ModEvents.EModEventResult.Continue;
            return false;
        }
    }

    /// <summary>Treat EULA as already accepted so startup never sticks on scroll/accept UI.</summary>
    [AutomationPatch]
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.HasAcceptedLatestEula))]
    static class Patch_HasAcceptedLatestEula
    {
        static bool Prefix(ref bool __result)
        {
            __result = true;
            return false;
        }
    }

    /// <summary>
    /// If EULA window still opens (default XML path), skip it and open main menu.
    /// Stock has XUiC_EulaWindow.Open(XUi,bool) but GUIWindowManager opens "windowEula" via the 3-arg (string,bool,bool) path,
    /// so we must patch both. Log shows wt open3 windowEula after CheckLogin force-open.
    /// </summary>
    [AutomationPatch]
    [HarmonyPatch(typeof(XUiC_EulaWindow), nameof(XUiC_EulaWindow.Open), new Type[] { typeof(XUi), typeof(bool) })]
    static class Patch_EulaWindow_Open
    {
        // Stock: Open(XUi _xui, bool _viewMode). Gate path uses _viewMode=false.
        static bool Prefix(XUi _xui, bool _viewMode)
        {
            try
            {
                int latest = EulaSkip.AcceptLatest();
                Log.Out("[7dtd-connect] blocking EULA window viewMode=" + _viewMode + " accepted=" + latest);
                if (_viewMode)
                    return true; // options "view EULA" path: leave alone
                if (_xui != null)
                    XUiC_MainMenu.Open(_xui);
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-connect] EULA skip failed: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>Steam-less Proton: GetAuthTicket throws InvalidOperationException when Steamworks not init; return empty ticket so SendLogin succeeds on EAC-off LAN.</summary>
    [AutomationPatch]
    [HarmonyPatch(typeof(Platform.Steam.AuthenticationClient), nameof(Platform.Steam.AuthenticationClient.GetAuthTicket))]
    static class Patch_SteamAuthTicket_Steamless
    {
        static bool Prefix(Platform.Steam.AuthenticationClient __instance, ref string __result)
        {
            // Pre-check: avoid calling SteamAPI if not running; the original's SteamUser.GetAuthSessionTicket throws InvalidOperationException.
            try
            {
                if (!Steamworks.SteamAPI.IsSteamRunning())
                {
                    __result = "";
                    Log.Out("[7dtd-connect] steam GetAuthTicket: no Steam, returning empty (EAC off LAN)");
                    return false;
                }
            }
            catch { __result = ""; return false; }
            return true;
        }
        // If original still throws, catch via Finalizer and supply empty result so SendLogin continues.
        static Exception Finalizer(Exception __exception, ref string __result)
        {
            if (__exception != null)
            {
                Log.Warning("[7dtd-connect] GetAuthTicket Finalizer: " + __exception.GetType().Name + " " + __exception.Message + " -> empty ticket");
                __result = "";
                return null;
            }
            return null;
        }
    }

    /// <summary>Steam-less Proton: client has no Steam/EOS identity, but stock dedi's PlayerIdAuthorizer kicks Empty name or player ID. Inject a synthetic local Steam id (derived from the machine name, so two Steam-less clients on different hosts never collide) so SendLogin succeeds on EAC-off LAN (loopback). With a real Steam login present, pass through so the server sees the real id and validates the real ticket.</summary>
    [AutomationPatch]
    [HarmonyPatch(typeof(Platform.Steam.User), nameof(Platform.Steam.User.PlatformUserId), MethodType.Getter)]
    static class Patch_SteamUserId_Synthetic
    {
        // Individual-account SteamID64 base; keep derived ids inside the
        // standard universe and far below the bot-id range BotTabPatch uses.
        const ulong IndividualAccountBase = 76561197960265728UL;

        static PlatformUserIdentifierAbs _fake;

        // Deterministic per host: stable across restarts on the same machine
        // (server-side player data persists), distinct across machines.
        static PlatformUserIdentifierAbs SyntheticId()
        {
            string seed = null;
            try { seed = Environment.MachineName; } catch { }
            if (string.IsNullOrWhiteSpace(seed))
            {
                try { seed = Environment.UserName; } catch { }
            }
            if (string.IsNullOrWhiteSpace(seed))
                return new Platform.Steam.UserIdentifierSteam("76561199000000042");
            ulong hash = 14695981039346656037UL;
            foreach (char c in seed.Trim())
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return new Platform.Steam.UserIdentifierSteam(
                (IndividualAccountBase + hash % 100000000UL).ToString());
        }

        static bool Prefix(Platform.Steam.User __instance, ref PlatformUserIdentifierAbs __result)
        {
            // Real Steam identity available -> let the original getter return it.
            // Direct Steamworks call (avoids re-entering this patched getter).
            try
            {
                if (Steamworks.SteamAPI.IsSteamRunning())
                {
                    var sid = Steamworks.SteamUser.GetSteamID();
                    if (sid.IsValid() && sid.m_SteamID != 0UL)
                        return true;
                }
            }
            catch { }
            try
            {
                if (_fake == null) _fake = SyntheticId();
                __result = _fake;
                return false;
            }
            catch { }
            return true;
        }
        static Exception Finalizer(Exception __exception, ref PlatformUserIdentifierAbs __result)
        {
            if (__exception != null)
            {
                try
                {
                    if (_fake == null) _fake = SyntheticId();
                    __result = _fake;
                }
                catch { }
                return null;
            }
            return null;
        }
    }

    [AutomationPatch]
    [HarmonyPatch(typeof(ClientInfo), "playerName", MethodType.Getter)]
    static class Patch_ClientInfo_PlayerName_Guard
    {
        static void Postfix(ClientInfo __instance, ref string __result)
        {
            if (!string.IsNullOrWhiteSpace(__result)) return;
            try
            {
                // Prefer GamePrefs name, then synthetic fallback.
                string pref = null;
                try { pref = GamePrefs.GetString(EnumGamePrefs.PlayerName); } catch { }
                if (!string.IsNullOrWhiteSpace(pref)) { __result = pref.Trim(); return; }
            }
            catch { }
            __result = PlayerNames.Resolve();
        }
    }

    // EOS path: patch concrete type directly (interface dispatch fails IL). The NRE is at Platform.EOS.AuthClient.GetAuthTicket when EOS not logged in.
    [AutomationPatch]
    [HarmonyPatch(typeof(Platform.EOS.AuthClient), "GetAuthTicket")]
    static class Patch_EOSAuthTicket_NotLoggedIn
    {
        static bool Prefix(ref string __result)
        {
            // EOS login present -> let the original produce a real ticket so the
            // server can validate it; only short-circuit when EOS is not logged in.
            try
            {
                var cross = Platform.PlatformManager.CrossplatformPlatform;
                var user = cross != null ? cross.User : null;
                if (user != null && user.PlatformUserId != null)
                    return true;
            }
            catch { }
            __result = "";
            return false;
        }
        static Exception Finalizer(Exception __exception, ref string __result)
        {
            if (__exception != null)
            {
                Log.Warning("[7dtd-connect] EOS GetAuthTicket Finalizer: " + __exception.GetType().Name + " " + __exception.Message + " -> empty");
                __result = "";
                return null;
            }
            return null;
        }
    }

    [AutomationPatch]
    [HarmonyPatch(typeof(GUIWindowManager), "Open", new Type[] { typeof(string), typeof(bool), typeof(bool) })]
    static class Patch_GuiWindow_EulaAsGate
    {
        static bool Prefix(GUIWindowManager __instance, string _windowName, bool _bModal, bool _bIsNotEscClosable)
        {
            return EulaSkip.BlockGateWindow(__instance, _windowName,
                "windowEula modal=" + _bModal + " esc=" + _bIsNotEscClosable);
        }
    }

    // Some paths open windowEula via (string,bool) - cover both arities
    [AutomationPatch]
    [HarmonyPatch(typeof(GUIWindowManager), "Open", new Type[] { typeof(string), typeof(bool) })]
    static class Patch_GuiWindow_EulaAsGate2
    {
        static bool Prefix(GUIWindowManager __instance, string _windowName, bool _bModal)
        {
            return EulaSkip.BlockGateWindow(__instance, _windowName,
                "windowEula(2) modal=" + _bModal);
        }
    }
}
