namespace SdtdConnect
{
    /// <summary>
    /// Shared math for the two load-gate heartbeats (spawn hb / load hb):
    /// stock updateLoadState starts the game once this many chunk game
    /// objects are displayed on scrolling worlds (fixed-size caches need
    /// none), so both probes must report the same bar or one hides the stall
    /// the other would have shown.
    /// </summary>
    internal static class LoadGate
    {
        internal static bool GameStarted(GameManager gm)
            => gm != null && gm.gameStateManager != null && gm.gameStateManager.IsGameStarted();

        // -1 names the menu phase (no chunk manager) in the raw log line.
        internal static int DisplayedChunkObjects(World world)
            => world.m_ChunkManager != null
                ? world.m_ChunkManager.GetDisplayedChunkGameObjectsCount() : -1;

        internal static bool FixedSizeCache(World world)
            => world.ChunkCache != null && world.ChunkCache.IsFixedSize;

        // Slack stock updateLoadState subtracts from viewDist^2 so a few
        // chunk objects still in flight cannot hold the start bar forever.
        const int StartBarSlackChunks = 10;

        // Stock updateLoadState's start bar, less the slack above.
        internal static int NeededChunkObjects(bool fixedSizeCache)
        {
            if (fixedSizeCache) return 0;
            int viewDist = GameUtils.GetViewDistance();
            return viewDist * viewDist - StartBarSlackChunks;
        }

        internal static bool TerrainReady
            => DistantTerrain.Instance == null || DistantTerrain.Instance.IsTerrainReady;
    }
}
