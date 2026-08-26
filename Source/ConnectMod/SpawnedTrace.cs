using System;
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
                    + "\n" + Environment.StackTrace);
            }
            catch (Exception ex)
            {
                // Prefix on a stock property setter: throwing here would
                // propagate into spawn handling and break the join this trace
                // exists to observe. Announce the first failure, then mute.
                ProbeFailure.Once("sp set trace", ex);
            }
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
                    + "\n" + Environment.StackTrace);
            }
            catch (Exception ex)
            {
                // Same contract as the setter trace above.
                ProbeFailure.Once("sp added trace", ex);
            }
        }
    }
}
