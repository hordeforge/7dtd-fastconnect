using System;
using HarmonyLib;
using UnityEngine;

namespace SdtdConnect
{
    /// <summary>
    /// In-game spawn/load heartbeat: logs the exact gates the
    /// "Starting game..." overlay checks, so a stuck join shows which
    /// condition never flips. Very chatty, so fully gated behind
    /// 7DTD_CONNECT_DEBUG=1 or `diag on`.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "gmUpdate")]
    static class Patch_GameManager_Update_SpawnHeartbeat
    {
        static float _nextLog;
        static void Postfix()
        {
            if (!DiagToggle.Enabled) return;
            if (Time.unscaledTime < _nextLog) return;
            _nextLog = Time.unscaledTime + DiagToggle.HeartbeatIntervalSec;
            try
            {
                // Unity stops rendering when the window loses focus, so an
                // external screenshot of an unfocused client shows a stale
                // frame from the loading phase. Keep frames coming.
                if (!Application.runInBackground) Application.runInBackground = true;

                var gm = GameManager.Instance;
                if (gm == null) return;
                var world = gm.World;
                if (world == null) return; // menu phase; boot hb covers it

                // One fetch per heartbeat feeds every dump below.
                var player = world.GetPrimaryPlayer();
                LogLoadGate(gm, world, player);
                if (player == null) return;

                LogMovement(player);
                LogRespawnUi(player);
                LogOpenWindows();
            }
            catch (Exception ex)
            {
                // A probe that always throws must not be silent, but it also
                // must not flood the log; ProbeFailure announces once.
                ProbeFailure.Once("spawn hb", ex);
            }
        }

        static void LogLoadGate(GameManager gm, World world, EntityPlayer player)
        {
            bool started = LoadGate.GameStarted(gm);
            int cgo = LoadGate.DisplayedChunkObjects(world);
            int viewDist = GameUtils.GetViewDistance();
            bool fixedSize = LoadGate.FixedSizeCache(world);
            int needed = LoadGate.NeededChunkObjects(fixedSize);
            bool terrainReady = LoadGate.TerrainReady;
            var ui = LocalPlayerUI.GetUIForPrimaryPlayer();
            bool xuiReady = ui != null && ui.xui != null && ui.xui.IsReady;

            Log.Out("[7dtd-fastconnect] spawn hb started=" + started
                + " cgo=" + cgo + "/" + needed
                + " fixedSize=" + fixedSize
                + " viewDist=" + viewDist
                + " terrainReady=" + terrainReady
                + " xuiReady=" + xuiReady
                + " player=" + (player != null ? player.entityId.ToString() : "null")
                + " pos=" + (player != null ? player.GetPosition().ToString() : "-"));
        }

        // Why the client does or does not send its position. GameManager
        // (the same method that ships PlayerStats / EntityAliveFlags every
        // tick) only sends EntityPosAndRot / RelPosAndRot when the integer
        // position differs from Entity.serverPos by at least
        // PositionSendThresholdBlocks on some axis. Stats arrive at the server
        // but position never does, so log both sides of that comparison plus
        // the spawn/remote flags feeding it.
        const int PositionSendThresholdBlocks = 2;

        // serverPos is fixed-point in stock wire units, NOT blocks: a player at
        // block -273 reads -8736 there. Compare in the same units or the delta
        // is nonsense. Convert with an arithmetic shift, which floors: plain
        // '/' truncates toward zero and reports raw -8752 as block -273 instead
        // of -274, skewing delta/wouldSend by one block on every
        // negative-coordinate frame.
        const int ServerPosFractionBits = 5;

        static void LogMovement(EntityPlayer player)
        {
            Vector3i b = World.worldToBlockPos(player.position);
            Vector3i srvRaw = player.serverPos;
            Vector3i srv = new Vector3i(
                srvRaw.x >> ServerPosFractionBits,
                srvRaw.y >> ServerPosFractionBits,
                srvRaw.z >> ServerPosFractionBits);
            Vector3i d = b - srv;
            bool wouldSend = Mathf.Abs(d.x) >= PositionSendThresholdBlocks
                || Mathf.Abs(d.y) >= PositionSendThresholdBlocks
                || Mathf.Abs(d.z) >= PositionSendThresholdBlocks;
            Log.Out("[7dtd-fastconnect] move hb posI=" + b + " serverPos=" + srv + " raw=" + srvRaw
                + " delta=" + d + " wouldSend=" + wouldSend
                + " spawned=" + player.IsSpawned() + " Spawned=" + player.Spawned
                + " remote=" + player.isEntityRemote
                + " attached=" + (player.AttachedToEntity != null)
                + " movementReplicated=" + player.IsMovementReplicated);
        }

        // updateLoadState only closes the spawn window group when
        // bChooseSpawnPosition is false, otherwise it shows the spawn-point map
        // and returns, so the loading background stays up with the world
        // already loaded. Log the inputs to that plus the respawn state the
        // game itself prints.
        static void LogRespawnUi(EntityPlayer player)
        {
            var pmc = player.GetComponent<PlayerMoveController>();
            if (pmc != null)
            {
                pmc.LogCurrentRespawnState();
                // State 4 branches on these: it compares the player's
                // position against spawnPosition and needs the spawn
                // window object to exist. Log both sides.
                var ssw = XUiC_SpawnSelectionWindow.GetWindow(LocalPlayerUI.primaryUI);
                Log.Out("[7dtd-fastconnect] pmc hb spawnPos=" + pmc.spawnPosition.position
                    + " undef=" + pmc.spawnPosition.IsUndef()
                    + " playerPos=" + player.position
                    + " equal=" + (player.position == pmc.spawnPosition.position)
                    + " waitingSel=" + pmc.waitingForSpawnPointSelection
                    + " sswNull=" + (ssw == null)
                    + " spawnMethod=" + (ssw != null ? ssw.spawnMethod.ToString() : "-"));
            }
            var ui = LocalPlayerUI.GetUIForPrimaryPlayer();
            var wm = ui != null ? ui.windowManager : null;
            Log.Out("[7dtd-fastconnect] ui hb respawnReason="
                + (pmc != null ? pmc.respawnReason.ToString() : "null")
                + " spawnWindowOpened=" + (pmc != null ? pmc.spawnWindowOpened.ToString() : "-")
                + " loadingScreen=" + (wm != null && wm.IsWindowOpen(XUiC_LoadingScreen.ID))
                + " spawnWin=" + (wm != null && wm.IsWindowOpen(XUiC_SpawnSelectionWindow.ID))
                + " progressWin=" + XUiC_ProgressWindow.IsWindowOpen());
        }

        // A client showing loading artwork while every window-ID check reports
        // closed means the open list is not the whole truth, so ask every
        // registered window whether it is actually showing. frameCount tells
        // apart "no new frames rendered" from "loading screen still up".
        static void LogOpenWindows()
        {
            string wins = "";
            var ui = LocalPlayerUI.GetUIForPrimaryPlayer();
            var wm = ui != null ? ui.windowManager : null;
            if (wm != null)
            {
                foreach (var kv in wm.nameToWindowMap)
                {
                    if (kv.Value != null && kv.Value.isShowing) wins += " " + kv.Key;
                }
            }
            Log.Out("[7dtd-fastconnect] win hb frame=" + Time.frameCount
                + " focused=" + Application.isFocused
                + " open:" + (wins.Length == 0 ? " (none)" : wins));
        }
    }
}
