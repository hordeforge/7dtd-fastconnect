using System;
using HarmonyLib;
using UnityEngine;

namespace SdtdConnect
{
    /// <summary>Spawn-selection heartbeat, opt-in via 7DTD_CONNECT_DEBUG or `diag on`.</summary>
    [HarmonyPatch(typeof(XUiC_SpawnSelectionWindow), "updateLoadState")]
    static class Patch_SpawnSelectionWindow_updateLoadState
    {
        static float _next;
        static int _calls;

        static void Prefix(XUiC_SpawnSelectionWindow __instance)
        {
            if (!DiagToggle.Enabled) return;
            _calls++;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + DiagToggle.HeartbeatIntervalSec;
            try
            {
                var gm = GameManager.Instance;
                var world = gm != null ? gm.World : null;
                int cgo = world != null ? LoadGate.DisplayedChunkObjects(world) : -1;
                bool fixedSize = world != null && LoadGate.FixedSizeCache(world);
                int needed = LoadGate.NeededChunkObjects(fixedSize);
                var cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
                Log.Out("[7dtd-fastconnect] load hb calls=" + _calls + " started="
                    + LoadGate.GameStarted(gm)
                    + " gameState=" + GameStats.GetInt(EnumGameStats.GameState)
                    + " delay=" + __instance.delayCountdownTime
                    + " cgo=" + cgo + "/" + needed
                    + " terrainReady=" + LoadGate.TerrainReady
                    + " isClient=" + (cm != null && cm.IsClient)
                    + " isServer=" + (cm != null && cm.IsServer)
                    + " uiNull=" + (LocalPlayerUI.GetUIForPrimaryPlayer() == null)
                    + " chooseSpawn=" + __instance.bChooseSpawnPosition
                    + " entering=" + __instance.bEnteringGame
                    + " firstTime=" + __instance.bFirstTimeSpawn);
            }
            catch (Exception ex)
            {
                // Same contract as the other heartbeats: announce the first
                // failure once, otherwise a broken probe looks like a healthy
                // quiet join.
                ProbeFailure.Once("load hb", ex);
            }
        }
    }
}
