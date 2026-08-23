using HarmonyLib;
using UnityEngine;

namespace SdtdConnect
{
    [HarmonyPatch(typeof(EntityAlive), "set_Spawned")]
    static class Patch_EntityAlive_SetSpawned
    {
        static void Prefix(EntityAlive __instance, bool value)
        {
            if (!DiagToggle.Enabled) return;
            try
            {
                if (!(__instance is EntityPlayerLocal)) return;
                Log.Out("[7dtd-fastconnect] sp set=" + value
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
            if (!DiagToggle.Enabled) return;
            try
            {
                if (!(__instance is EntityPlayerLocal)) return;
                Log.Out("[7dtd-fastconnect] sp added remote=" + __instance.isEntityRemote
                    + " Spawned=" + __instance.Spawned
                    + " t=" + Time.unscaledTime
                    + "\n" + System.Environment.StackTrace);
            }
            catch { }
        }
    }
}
