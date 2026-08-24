using System;
using HarmonyLib;
using UnityEngine;

namespace SdtdConnect
{
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
                Log.Warning("[7dtd-fastconnect] MainMenu.Open prefix failed: " + ex.Message);
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
        static bool _failLogged;

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
                Log.Out("[7dtd-fastconnect] boot hb ticks=" + _ticks
                    + " focused=" + Application.isFocused
                    + " rib=" + Application.runInBackground
                    + " vsync=" + QualitySettings.vSyncCount
                    + " tfr=" + Application.targetFrameRate
                    + " static=" + loaded
                    + " loginDone=" + (__instance != null && __instance.loginCheckDone)
                    + " openMM=" + (__instance != null && __instance.bOpenMainMenu)
                    + " action=" + action);
            }
            catch (Exception ex)
            {
                // Same contract as the spawn/load heartbeats: a probe that
                // always throws must not be silent, or it looks like a healthy
                // quiet boot. Announce the first failure once, then mute.
                if (!_failLogged)
                {
                    _failLogged = true;
                    try { Log.Warning("[7dtd-fastconnect] boot hb failed (further failures muted):\n" + ex); }
                    catch { }
                }
            }
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
                Log.Out("[7dtd-fastconnect] CheckLogin force-open main menu (automation)");
                try
                {
                    var xui = __instance.windowManager?.playerUI?.xui;
                    if (xui != null)
                        XUiC_LoginBase.Login(xui, () => { /* stock OnLoginComplete path optional */ });
                }
                catch (Exception ex)
                {
                    Log.Warning("[7dtd-fastconnect] background Login start failed: " + ex.Message);
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] CheckLogin patch failed: " + ex.Message);
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
                Log.Out("[7dtd-fastconnect] blocking news screen open");
                XUiC_MainMenu.shownNewsScreenOnce = true;
                // Fall through to main menu instead of news.
                if (_xuiInstance != null)
                    XUiC_MainMenu.Open(_xuiInstance);
                return false; // skip original NewsScreen.Open
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] news skip failed: " + ex.Message);
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
            Log.Out("[7dtd-fastconnect] skipping DiscordManager.Init");
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
                Log.Out("[7dtd-fastconnect] blocking EULA window viewMode=" + _viewMode + " accepted=" + latest);
                if (_viewMode)
                    return true; // options "view EULA" path: leave alone
                if (_xui != null)
                    XUiC_MainMenu.Open(_xui);
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] EULA skip failed: " + ex.Message);
                return true;
            }
        }
    }

    [AutomationPatch]
    [HarmonyPatch(typeof(GUIWindowManager), "Open", new Type[] { typeof(string), typeof(bool), typeof(bool) })]
    static class Patch_GuiWindow_EulaAsGate
    {
        static bool Prefix(GUIWindowManager __instance, string _windowName, bool _bModal, bool _bIsNotEscClosable)
        {
            // Name guard before the logTag concat: Open fires for every UI
            // window (toolTip/saveIndicator per tick), so the non-EULA path
            // must stay allocation-free.
            if (_windowName != EulaSkip.GateWindowName) return true;
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
            // Same allocation-free non-EULA path as the 3-arity gate above.
            if (_windowName != EulaSkip.GateWindowName) return true;
            return EulaSkip.BlockGateWindow(__instance, _windowName,
                "windowEula(2) modal=" + _bModal);
        }
    }
}
