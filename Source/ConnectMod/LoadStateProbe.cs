using HarmonyLib;
using UnityEngine;

namespace ZdtdConnect
{
    /// <summary>
    /// The join stalls with PlayerMoveController in WaitingForSpawnWindowToClose:
    /// the spawn-selection window is open and updateLoadState runs every frame,
    /// yet never reaches its Close. Log the value of every gate it returns on so
    /// the blocking one is named rather than inferred.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_SpawnSelectionWindow), "updateLoadState")]
    static class Patch_SpawnSelectionWindow_updateLoadState
    {
        static float _next;
        static int _calls;

        static void Prefix(XUiC_SpawnSelectionWindow __instance)
        {
            // Count every call: one log line per 5s cannot tell "ticking but
            // always bailing on the chunk gate" from "stopped being ticked".
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
                Log.Out("[zdtd-connect] load hb calls=" + _calls + " started="
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
            catch { /* diagnostics only */ }
        }
    }
}
