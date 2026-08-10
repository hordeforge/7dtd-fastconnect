using HarmonyLib;
using UnityEngine;

namespace ZdtdConnect
{
    /// <summary>
    /// The join stalls with the spawn-selection window still open, while the
    /// same client against the stock server closes it within a second. Trace
    /// every window open/close so the two sequences can be diffed directly.
    /// The game uses the (string, bool) and (GUIWindow, bool) overloads, so all
    /// four have to be hooked or the interesting calls are invisible.
    /// </summary>
    [HarmonyPatch(typeof(GUIWindowManager), "Open", new[] { typeof(string), typeof(bool) })]
    static class Patch_GUIWindowManager_OpenName2
    {
        static void Prefix(string _windowName)
        {
            try { Log.Out("[zdtd-connect] wt open " + _windowName + " t=" + Time.unscaledTime); } catch { }
        }
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Open", new[] { typeof(string), typeof(bool), typeof(bool) })]
    static class Patch_GUIWindowManager_OpenName3
    {
        static void Prefix(string _windowName)
        {
            try { Log.Out("[zdtd-connect] wt open3 " + _windowName + " t=" + Time.unscaledTime); } catch { }
        }
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Open", new[] { typeof(GUIWindow), typeof(bool) })]
    static class Patch_GUIWindowManager_OpenWin
    {
        static void Prefix(GUIWindow _w)
        {
            try { Log.Out("[zdtd-connect] wt openW " + (_w != null ? _w.Id : "null") + " t=" + Time.unscaledTime); } catch { }
        }
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Close", new[] { typeof(GUIWindow), typeof(bool) })]
    static class Patch_GUIWindowManager_CloseWin
    {
        static void Prefix(GUIWindow _w)
        {
            try { Log.Out("[zdtd-connect] wt closeW " + (_w != null ? _w.Id : "null") + " t=" + Time.unscaledTime); } catch { }
        }
    }

    [HarmonyPatch(typeof(GUIWindowManager), "Close", new[] { typeof(string) })]
    static class Patch_GUIWindowManager_CloseName
    {
        static void Prefix(string _windowName)
        {
            try { Log.Out("[zdtd-connect] wt close " + _windowName + " t=" + Time.unscaledTime); } catch { }
        }
    }
}
