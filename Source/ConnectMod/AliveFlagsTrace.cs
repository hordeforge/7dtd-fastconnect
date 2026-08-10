using HarmonyLib;
using UnityEngine;

namespace ZdtdConnect
{
    /// <summary>
    /// EntityAlive.Spawned flips true on the local player without updateRespawn
    /// ever logging "Respawn almost done", so something else sets it. Bit 0x08
    /// of NetPackageEntityAliveFlags is the only other writer reachable over the
    /// wire: log every one that targets the local player.
    /// </summary>
    [HarmonyPatch(typeof(NetPackageEntityAliveFlags), "ProcessPackage")]
    static class Patch_NetPackageEntityAliveFlags_Process
    {
        static void Prefix(NetPackageEntityAliveFlags __instance)
        {
            try
            {
                var gm = GameManager.Instance;
                var p = gm != null && gm.World != null ? gm.World.GetPrimaryPlayer() : null;
                if (p == null || __instance.entityId != p.entityId) return;
                Log.Out("[zdtd-connect] af hb entity=" + __instance.entityId
                    + " flags=" + __instance.flags
                    + " spawnedBit=" + ((__instance.flags & 8) > 0)
                    + " t=" + Time.unscaledTime);
            }
            catch { }
        }
    }
}
