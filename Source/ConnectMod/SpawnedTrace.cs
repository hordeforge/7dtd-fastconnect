using HarmonyLib;
using UnityEngine;

namespace ZdtdConnect
{
    static class SpawnedTraceConfig
    {
        public static bool Enabled => DiagToggle.Enabled;
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
