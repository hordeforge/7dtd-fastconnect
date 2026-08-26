using System;
using HarmonyLib;
using UnityEngine;

namespace SdtdConnect
{
    /// <summary>AliveFlags trace, opt-in via 7DTD_CONNECT_DEBUG or `diag on`.</summary>
    [HarmonyPatch(typeof(NetPackageEntityAliveFlags), "ProcessPackage")]
    static class Patch_NetPackageEntityAliveFlags_Process
    {
        // Stock EntityAlive flag bit reporting "spawned" in the wire flags.
        const int SpawnedFlagBit = 8;

        static void Prefix(NetPackageEntityAliveFlags __instance)
        {
            if (!DiagToggle.Enabled) return;
            try
            {
                var gm = GameManager.Instance;
                var p = gm != null && gm.World != null ? gm.World.GetPrimaryPlayer() : null;
                if (p == null || __instance.entityId != p.entityId) return;
                Log.Out("[7dtd-fastconnect] af hb entity=" + __instance.entityId
                    + " flags=" + __instance.flags
                    + " spawnedBit=" + ((__instance.flags & SpawnedFlagBit) > 0)
                    + " t=" + Time.unscaledTime);
            }
            catch (Exception ex)
            {
                // Runs inside a stock packet handler on every alive-flags
                // package: a throwing trace must not take the join down with
                // it, but a permanently dead one must not read as a quiet
                // healthy join either. ProbeFailure announces once, then mutes.
                ProbeFailure.Once("af hb", ex);
            }
        }
    }
}
