using System;
using HarmonyLib;
using UnityEngine;

namespace SdtdConnect
{
    /// <summary>
    /// Window open/close trace for join-diagnostics. Very spammy in normal play
    /// (toolTip/saveIndicator fire every tick), so fully gated behind
    /// 7DTD_CONNECT_DEBUG or `diag on`; when on, log everything including spam.
    /// </summary>
    [HarmonyPatch(typeof(GUIWindowManager), "Open", new[] { typeof(string), typeof(bool) })]
    static class Patch_GUIWindowManager_OpenName2
    {
        static void Prefix(string _windowName)
        {
            if (!DiagToggle.Enabled) return;
            try { Log.Out("[7dtd-connect] wt open " + _windowName + " t=" + Time.unscaledTime); } catch { }
        }
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Open", new[] { typeof(string), typeof(bool), typeof(bool) })]
    static class Patch_GUIWindowManager_OpenName3
    {
        static void Prefix(string _windowName)
        {
            if (!DiagToggle.Enabled) return;
            try { Log.Out("[7dtd-connect] wt open3 " + _windowName + " t=" + Time.unscaledTime); } catch { }
        }
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Open", new[] { typeof(GUIWindow), typeof(bool) })]
    static class Patch_GUIWindowManager_OpenWin
    {
        static void Prefix(GUIWindow _w)
        {
            if (!DiagToggle.Enabled) return;
            string id = _w != null ? _w.Id : "null";
            try { Log.Out("[7dtd-connect] wt openW " + id + " t=" + Time.unscaledTime); } catch { }
        }
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Close", new[] { typeof(GUIWindow), typeof(bool) })]
    static class Patch_GUIWindowManager_CloseWin
    {
        static void Prefix(GUIWindow _w)
        {
            if (!DiagToggle.Enabled) return;
            string id = _w != null ? _w.Id : "null";
            try { Log.Out("[7dtd-connect] wt closeW " + id + " t=" + Time.unscaledTime); } catch { }
        }
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Close", new[] { typeof(string) })]
    static class Patch_GUIWindowManager_CloseName
    {
        static void Prefix(string _windowName)
        {
            if (!DiagToggle.Enabled) return;
            try { Log.Out("[7dtd-connect] wt close " + _windowName + " t=" + Time.unscaledTime); } catch { }
        }
    }
}
