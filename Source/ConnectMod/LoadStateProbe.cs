using System;
using HarmonyLib;
using UnityEngine;

namespace SdtdConnect
{
    /// <summary>Spawn-selection heartbeat — opt-in via 7DTD_CONNECT_DEBUG or `diag on`.</summary>
    [HarmonyPatch(typeof(XUiC_SpawnSelectionWindow), "updateLoadState")]
    static class Patch_SpawnSelectionWindow_updateLoadState
    {
        static float _next;
        static int _calls;
        static bool _failLogged;

        static void Prefix(XUiC_SpawnSelectionWindow __instance)
        {
            if (!DiagToggle.Enabled) return;
            _calls++;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 5f;
            try
            {
                var gm = GameManager.Instance;
                var world = gm != null ? gm.World : null;
                int cgo = world != null && world.m_ChunkManager != null
                    ? world.m_ChunkManager.GetDisplayedChunkGameObjectsCount() : -1;
                int vd = GameUtils.GetViewDistance();
                bool fixedSize = world != null && world.ChunkCache != null && world.ChunkCache.IsFixedSize;
                int needed = fixedSize ? 0 : vd * vd - 10;
                var cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
                Log.Out("[7dtd-connect] load hb calls=" + _calls + " started="
                    + (gm != null && gm.gameStateManager != null && gm.gameStateManager.IsGameStarted())
                    + " gameState=" + GameStats.GetInt(EnumGameStats.GameState)
                    + " delay=" + __instance.delayCountdownTime
                    + " cgo=" + cgo + "/" + needed
                    + " terrainReady=" + (DistantTerrain.Instance == null || DistantTerrain.Instance.IsTerrainReady)
                    + " isClient=" + (cm != null && cm.IsClient)
                    + " isServer=" + (cm != null && cm.IsServer)
                    + " uiNull=" + (LocalPlayerUI.GetUIForPrimaryPlayer() == null)
                    + " chooseSpawn=" + __instance.bChooseSpawnPosition
                    + " entering=" + __instance.bEnteringGame
                    + " firstTime=" + __instance.bFirstTimeSpawn);
            }
            catch (Exception ex)
            {
                // Same as the spawn heartbeat: announce the first failure once,
                // otherwise a broken probe looks like a healthy quiet join.
                if (!_failLogged)
                {
                    _failLogged = true;
                    try { Log.Warning("[7dtd-connect] load hb failed (further failures muted):\n" + ex); }
                    catch { }
                }
            }
        }
    }
}
