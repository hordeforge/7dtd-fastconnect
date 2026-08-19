using System;
using HarmonyLib;
using UnityEngine;

namespace ZdtdConnect
{
    /// <summary>
    /// Window open/close trace for join-diagnostics. Very spammy in normal play
    /// (toolTip/saveIndicator fire every tick), so this is now opt-in via
    /// ZDTD_CONNECT_DEBUG or `diag on`.
    /// </summary>
    static class WindowTraceConfig
    {
        public static bool ShouldLog(string id)
        {
            if (!DiagToggle.Enabled) return false;
            // When enabled, log everything including spam (that's the point of debug).
            return true;
        }
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Open", new[] { typeof(string), typeof(bool) })]
    static class Patch_GUIWindowManager_OpenName2
    {
        static void Prefix(string _windowName)
        {
            if (!WindowTraceConfig.ShouldLog(_windowName)) return;
            try { Log.Out("[zdtd-connect] wt open " + _windowName + " t=" + Time.unscaledTime); } catch { }
        }
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Open", new[] { typeof(string), typeof(bool), typeof(bool) })]
    static class Patch_GUIWindowManager_OpenName3
    {
        static void Prefix(string _windowName)
        {
            if (!WindowTraceConfig.ShouldLog(_windowName)) return;
            try { Log.Out("[zdtd-connect] wt open3 " + _windowName + " t=" + Time.unscaledTime); } catch { }
        }
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Open", new[] { typeof(GUIWindow), typeof(bool) })]
    static class Patch_GUIWindowManager_OpenWin
    {
        static void Prefix(GUIWindow _w)
        {
            string id = _w != null ? _w.Id : "null";
            if (!WindowTraceConfig.ShouldLog(id)) return;
            try { Log.Out("[zdtd-connect] wt openW " + id + " t=" + Time.unscaledTime); } catch { }
        }
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Close", new[] { typeof(GUIWindow), typeof(bool) })]
    static class Patch_GUIWindowManager_CloseWin
    {
        static void Prefix(GUIWindow _w)
        {
            string id = _w != null ? _w.Id : "null";
            if (!WindowTraceConfig.ShouldLog(id)) return;
            try { Log.Out("[zdtd-connect] wt closeW " + id + " t=" + Time.unscaledTime); } catch { }
        }
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Close", new[] { typeof(string) })]
    static class Patch_GUIWindowManager_CloseName
    {
        static void Prefix(string _windowName)
        {
            if (!WindowTraceConfig.ShouldLog(_windowName)) return;
            try { Log.Out("[zdtd-connect] wt close " + _windowName + " t=" + Time.unscaledTime); } catch { }
        }
    }
}
