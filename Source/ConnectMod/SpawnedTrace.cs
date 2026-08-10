using HarmonyLib;
using UnityEngine;

namespace ZdtdConnect
{
    /// <summary>
    /// updateRespawn short-circuits to Done because EntityAlive.Spawned is
    /// already true, so it never reaches the block that closes the loading
    /// screen. Log every write to Spawned on the local player with a stack
    /// trace: the caller identifies which server package (or client path)
    /// flips it mid-sequence.
    /// </summary>
    [HarmonyPatch(typeof(EntityAlive), "set_Spawned")]
    static class Patch_EntityAlive_SetSpawned
    {
        static void Prefix(EntityAlive __instance, bool value)
        {
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

    /// <summary>
    /// OnAddedToWorld writes bSpawned directly, bypassing the property setter,
    /// so the setter hook alone can miss the flip.
    /// </summary>
    [HarmonyPatch(typeof(EntityAlive), "OnAddedToWorld")]
    static class Patch_EntityAlive_OnAddedToWorld
    {
        static void Postfix(EntityAlive __instance)
        {
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
