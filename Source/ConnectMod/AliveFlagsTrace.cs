using HarmonyLib;
using UnityEngine;

namespace ZdtdConnect
{
    /// <summary>AliveFlags trace — opt-in via ZDTD_CONNECT_DEBUG or `diag on`.</summary>
    [HarmonyPatch(typeof(NetPackageEntityAliveFlags), "ProcessPackage")]
    static class Patch_NetPackageEntityAliveFlags_Process
    {
        static void Prefix(NetPackageEntityAliveFlags __instance)
        {
            if (!DiagToggle.Enabled) return;
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
