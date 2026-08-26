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
    static class WindowTrace
    {
        /// <summary>
        /// One guarded emit for every GUIWindowManager hook below. These are
        /// prefixes on stock window plumbing, so a throwing trace would take
        /// the UI down; ProbeFailure announces the first failure once so a
        /// permanently dead trace cannot read as "no windows opened".
        /// </summary>
        internal static void Emit(string what, string subject)
        {
            if (!DiagToggle.Enabled) return;
            try
            {
                Log.Out("[7dtd-fastconnect] wt " + what + " " + subject
                    + " t=" + Time.unscaledTime);
            }
            catch (Exception ex)
            {
                ProbeFailure.Once("wt " + what, ex);
            }
        }

        internal static string Name(GUIWindow w) => w != null ? w.Id : "null";
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Open", new[] { typeof(string), typeof(bool) })]
    static class Patch_GUIWindowManager_OpenName2
    {
        static void Prefix(string _windowName) => WindowTrace.Emit("open", _windowName);
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Open", new[] { typeof(string), typeof(bool), typeof(bool) })]
    static class Patch_GUIWindowManager_OpenName3
    {
        static void Prefix(string _windowName) => WindowTrace.Emit("open3", _windowName);
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Open", new[] { typeof(GUIWindow), typeof(bool) })]
    static class Patch_GUIWindowManager_OpenWin
    {
        static void Prefix(GUIWindow _w) => WindowTrace.Emit("openW", WindowTrace.Name(_w));
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Close", new[] { typeof(GUIWindow), typeof(bool) })]
    static class Patch_GUIWindowManager_CloseWin
    {
        static void Prefix(GUIWindow _w) => WindowTrace.Emit("closeW", WindowTrace.Name(_w));
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Close", new[] { typeof(string) })]
    static class Patch_GUIWindowManager_CloseName
    {
        static void Prefix(string _windowName) => WindowTrace.Emit("close", _windowName);
    }
}
