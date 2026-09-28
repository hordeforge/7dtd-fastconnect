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
        // Snapshot once: the process env cannot change at runtime, and this is
        // read on the local-host hold/release path, which runs per stage.
        static bool? _forceSyncEnabled;

        // Reflection target for LoadManager.forceLoadSync, resolved once and
        // shared with LocalHostWorldLoad's hold/release wrapper so the
        // automation set-once path and the local-host hold/release path cannot
        // drift apart when a game update renames the field (and cannot flood
        // the log with repeated missing-field warnings either).
        static FieldInfo _forceSyncField;
        static bool _forceSyncFieldResolved;

        internal static FieldInfo ForceLoadSyncField()
        {
            if (_forceSyncFieldResolved) return _forceSyncField;
            _forceSyncFieldResolved = true;
            var fi = typeof(LoadManager).GetField("forceLoadSync",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi == null || fi.FieldType != typeof(bool))
            {
                Log.Warning("[7dtd-fastconnect] LoadManager.forceLoadSync field missing");
                return null;
            }
            _forceSyncField = fi;
            return fi;
        }

        internal static bool ForceLoadSyncEnabled()
        {
            if (_forceSyncEnabled.HasValue) return _forceSyncEnabled.Value;
            // Opt-out flag shape: unset/blank keeps the automation default
            // (enabled); only an explicit 0/false/no/off opts out. An
            // unreadable environment counts as unset, same as blank.
            _forceSyncEnabled = !EnvFlags.VarIsOptOut(ForceLoadSyncEnv);
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
                // Every-frame caller: UpdateFPSCap re-applies the cap between
                // frames, so a throwing property setter would otherwise write
                // one warning per frame, thousands per second while boot runs
                // uncapped. The reason keys the latch, so a failure from one
                // hook cannot mute the same failure from another.
                ProbeFailure.Once("frame uncap (" + reason + ")", ex);
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
                var fi = ForceLoadSyncField();
                if (fi == null) return;
                fi.SetValue(null, true);
                _forceSyncSet = true;
                Log.Out("[7dtd-fastconnect] LoadManager.forceLoadSync=true (automation addressables)");
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] forceLoadSync set failed: " + ex.GetType().Name + ": " + ex.Message);
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
