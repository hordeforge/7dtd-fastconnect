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
                Log.Warning("[7dtd-fastconnect] frame uncap failed (" + reason + "): " + ex.Message);
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
                    Log.Out("[7dtd-fastconnect] LoadManager.forceLoadSync disabled by "
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
                    Log.Warning("[7dtd-fastconnect] LoadManager.forceLoadSync field missing");
                    return;
                }
                fi.SetValue(null, true);
                _forceSyncSet = true;
                Log.Out("[7dtd-fastconnect] LoadManager.forceLoadSync=true (automation addressables)");
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] forceLoadSync set failed: " + ex.Message);
            }
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
            Log.Out("[7dtd-fastconnect] boot unblock RIB+noVSync+uncappedFPS");
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
}
