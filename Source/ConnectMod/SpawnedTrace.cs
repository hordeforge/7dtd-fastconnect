using HarmonyLib;
using UnityEngine;

namespace ZdtdConnect
{
    /// <summary>
    /// Spawned trace for join-diagnostics. Full stack traces every frame are
    /// brutal in normal play, so this is now opt-in via ZDTD_CONNECT_DEBUG=1.
    /// </summary>
    static class SpawnedTraceConfig
    {
        public static bool Enabled
        {
            get
            {
                try
                {
                    var v = System.Environment.GetEnvironmentVariable("ZDTD_CONNECT_DEBUG");
                    return v == "1" || v == "true";
                }
                catch { return false; }
            }
        }
    }

    [HarmonyPatch(typeof(EntityAlive), "set_Spawned")]
    static class Patch_EntityAlive_SetSpawned
    {
        static void Prefix(EntityAlive __instance, bool value)
        {
            if (!SpawnedTraceConfig.Enabled) return;
            try
            {
                if (!(__instance is EntityPlayerLocal)) return;
                Log.Out("[zdtd-connect] sp set=" + value
                    + " was=" + __instance.Spawned
                    + " t=" + Time.unscaledTime
                    + "\n" + System.Environment.StackTrace);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(EntityAlive), "OnAddedToWorld")]
    static class Patch_EntityAlive_OnAddedToWorld
    {
        static void Postfix(EntityAlive __instance)
        {
            if (!SpawnedTraceConfig.Enabled) return;
            try
            {
                if (!(__instance is EntityPlayerLocal)) return;
                Log.Out("[zdtd-connect] sp added remote=" + __instance.isEntityRemote
                    + " Spawned=" + __instance.Spawned
                    + " t=" + Time.unscaledTime
                    + "\n" + System.Environment.StackTrace);
            }
            catch { }
        }
    }
}
