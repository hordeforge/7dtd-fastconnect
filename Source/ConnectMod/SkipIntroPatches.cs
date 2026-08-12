using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ZdtdConnect
{
    /// <summary>
    /// Proton/headless: keep main thread + addressables moving.
    /// Stock RIB only in editor; VSync/FPS cap + async addressables stall at ~1 FPS.
    /// forceLoadSync makes LoadManager WaitForCompletion (same as dedi path).
    /// </summary>
    static class BootUnblock
    {
        static bool _forceSyncSet;

        internal static void ApplyFrameUncap(string reason)
        {
            try
            {
                Application.runInBackground = true;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                Application.backgroundLoadingPriority = ThreadPriority.High;
            }
            catch (Exception ex)
            {
                Log.Warning("[zdtd-connect] frame uncap failed (" + reason + "): " + ex.Message);
            }
        }

        internal static void ApplyForceLoadSync()
        {
            if (_forceSyncSet) return;
            try
            {
                var fi = typeof(LoadManager).GetField("forceLoadSync",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi == null || fi.FieldType != typeof(bool))
                {
                    Log.Warning("[zdtd-connect] LoadManager.forceLoadSync field missing");
                    return;
                }
                fi.SetValue(null, true);
                _forceSyncSet = true;
                Log.Out("[zdtd-connect] LoadManager.forceLoadSync=true (automation addressables)");
            }
            catch (Exception ex)
            {
                Log.Warning("[zdtd-connect] forceLoadSync set failed: " + ex.Message);
            }
        }
    }

    [HarmonyPatch(typeof(GameManager), "Awake")]
    static class Patch_GameManager_Awake_RunInBackground
    {
        static void Postfix()
        {
            BootUnblock.ApplyFrameUncap("Awake");
            BootUnblock.ApplyForceLoadSync();
            Log.Out("[zdtd-connect] boot unblock RIB+noVSync+uncappedFPS+forceLoadSync");
        }
    }

    /// <summary>Stock UpdateFPSCap re-applies VSync refresh cap before GameHasStarted; keep uncapped.</summary>
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
    [HarmonyPatch(typeof(XUiC_MainMenu), nameof(XUiC_MainMenu.Open), new Type[] { typeof(XUi) })]
    static class Patch_MainMenu_Open
    {
        static void Prefix()
        {
            // Stock Open() opens NewsScreen when shownNewsScreenOnce is false.
            XUiC_MainMenu.shownNewsScreenOnce = true;
            try
            {
                BootUnblock.ApplyFrameUncap("MainMenu.Open");
                if (GameManager.Instance != null)
                    GameManager.Instance.showOpenerMovieOnLoad = false;
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Boot progress heartbeat so join harness can see static-load stalls.
    /// </summary>
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
                Log.Out("[zdtd-connect] boot hb ticks=" + _ticks
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
                Log.Out("[zdtd-connect] CheckLogin force-open main menu (automation)");
                try
                {
                    var xui = __instance.windowManager?.playerUI?.xui;
                    if (xui != null)
                        XUiC_LoginBase.Login(xui, () => { /* stock OnLoginComplete path optional */ });
                }
                catch (Exception ex)
                {
                    Log.Warning("[zdtd-connect] background Login start failed: " + ex.Message);
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[zdtd-connect] CheckLogin patch failed: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>If news is already open (race / earlier open path), close it and open main menu.</summary>
    [HarmonyPatch(typeof(XUiC_NewsScreen), nameof(XUiC_NewsScreen.Open), new Type[] { typeof(XUi) })]
    static class Patch_NewsScreen_Open
    {
        // Stock signature uses _xuiInstance (not _xui); Harmony matches by name.
        static bool Prefix(XUi _xuiInstance)
        {
            try
            {
                Log.Out("[zdtd-connect] blocking news screen open");
                XUiC_MainMenu.shownNewsScreenOnce = true;
                // Fall through to main menu instead of news.
                if (_xuiInstance != null)
                    XUiC_MainMenu.Open(_xuiInstance);
                return false; // skip original NewsScreen.Open
            }
            catch (Exception ex)
            {
                Log.Warning("[zdtd-connect] news skip failed: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>Never initialize Discord SDK / login (RPC connect spam and login UI).</summary>
    [HarmonyPatch(typeof(DiscordManager), nameof(DiscordManager.Init))]
    static class Patch_DiscordManager_Init
    {
        static bool Prefix()
        {
            Log.Out("[zdtd-connect] skipping DiscordManager.Init");
            return false;
        }
    }

    /// <summary>Do not open first-time Discord info / login window on main menu.</summary>
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
    [HarmonyPatch(typeof(XUiC_EulaWindow), nameof(XUiC_EulaWindow.Open), new Type[] { typeof(XUi), typeof(bool) })]
    static class Patch_EulaWindow_Open
    {
        // Stock: Open(XUi _xui, bool _viewMode). Gate path uses _viewMode=false.
        static bool Prefix(XUi _xui, bool _viewMode)
        {
            try
            {
                int latest = GamePrefs.GetInt(EnumGamePrefs.EulaLatestVersion);
                if (latest < 1) latest = 99;
                GamePrefs.Set(EnumGamePrefs.EulaLatestVersion, latest);
                GamePrefs.Set(EnumGamePrefs.EulaVersionAccepted, latest);
                GamePrefs.Instance?.Save();
                Log.Out("[zdtd-connect] blocking EULA window viewMode=" + _viewMode + " accepted=" + latest);
                if (_viewMode)
                    return true; // options "view EULA" path: leave alone
                if (_xui != null)
                    XUiC_MainMenu.Open(_xui);
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[zdtd-connect] EULA skip failed: " + ex.Message);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Open", new Type[] { typeof(string), typeof(bool), typeof(bool) })]
    static class Patch_GuiWindow_EulaAsGate
    {
        static bool Prefix(GUIWindowManager __instance, string _windowName, bool _bModal, bool _bIsNotEscClosable)
        {
            if (_windowName != "windowEula") return true;
            try
            {
                Log.Out($"[zdtd-connect] blocking GUI windowEula modal={_bModal} esc={_bIsNotEscClosable}");
                int latest = GamePrefs.GetInt(EnumGamePrefs.EulaLatestVersion);
                if (latest < 1) latest = 99;
                GamePrefs.Set(EnumGamePrefs.EulaLatestVersion, latest);
                GamePrefs.Set(EnumGamePrefs.EulaVersionAccepted, latest);
                GamePrefs.Instance?.Save();
                try
                {
                    var xui = __instance?.playerUI?.xui;
                    if (xui != null) XUiC_MainMenu.Open(xui);
                    // Also dispatch the ModEvent directly so auto-join fires even if XUi path is gated.
                    try
                    {
                        var data = new ModEvents.SMainMenuOpenedData(true);
                        ModEvents.MainMenuOpened.Invoke(ref data);
                        Log.Out("[zdtd-connect] dispatched MainMenuOpened after Eula block (3)");
                    }
                    catch (Exception ex2) { Log.Warning("[zdtd-connect] MainMenuOpened dispatch failed (3): " + ex2.Message); }
                }
                catch { }
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[zdtd-connect] windowEula block failed: " + ex.Message);
                return true;
            }
        }
    }

    // Some paths open windowEula via (string,bool) - cover both arities
    [HarmonyPatch(typeof(GUIWindowManager), "Open", new Type[] { typeof(string), typeof(bool) })]
    static class Patch_GuiWindow_EulaAsGate2
    {
        static bool Prefix(GUIWindowManager __instance, string _windowName, bool _bModal)
        {
            if (_windowName != "windowEula") return true;
            try
            {
                Log.Out($"[zdtd-connect] blocking GUI windowEula(2) modal={_bModal}");
                int latest = GamePrefs.GetInt(EnumGamePrefs.EulaLatestVersion);
                if (latest < 1) latest = 99;
                GamePrefs.Set(EnumGamePrefs.EulaLatestVersion, latest);
                GamePrefs.Set(EnumGamePrefs.EulaVersionAccepted, latest);
                GamePrefs.Instance?.Save();
                try
                {
                    var xui = __instance?.playerUI?.xui;
                    if (xui != null) XUiC_MainMenu.Open(xui);
                    try
                    {
                        var data = new ModEvents.SMainMenuOpenedData(true);
                        ModEvents.MainMenuOpened.Invoke(ref data);
                        Log.Out("[zdtd-connect] dispatched MainMenuOpened after Eula block (2)");
                    }
                    catch (Exception ex2) { Log.Warning("[zdtd-connect] MainMenuOpened dispatch failed (2): " + ex2.Message); }
                }
                catch { }
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[zdtd-connect] windowEula(2) block failed: " + ex.Message);
                return true;
            }
        }
    }
}


