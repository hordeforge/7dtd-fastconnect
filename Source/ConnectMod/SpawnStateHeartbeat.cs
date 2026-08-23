using System;
using System.IO;
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
        static int _shots;
        static bool _failLogged;
        static void Postfix()
        {
            if (!DiagToggle.Enabled) return;
            if (Time.unscaledTime < _nextLog) return;
            _nextLog = Time.unscaledTime + 5f;
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

                LogLoadGate(gm, world);

                var player = world.GetPrimaryPlayer();
                if (player == null) return;

                // Block coords anchor every per-player dump below.
                Vector3i b = World.worldToBlockPos(player.position);
                LogMovement(world, player, b);
                LogBlockColumn(world, b);
                LogCollisionMesh(world, player, b);
                LogNeighbourRing(world, b);
                LogWorldSamples(world, player, b);
                LogCacheWindow(world, b);
                LogRespawnUi(player);
                LogOpenWindows();
                TryCaptureScreenshot(player);
            }
            catch (Exception ex)
            {
                // Silence here is indistinguishable from a healthy quiet join;
                // announce the first failure so the diagnostic cannot defeat
                // itself, then stop spamming.
                if (!_failLogged)
                {
                    _failLogged = true;
                    try { Log.Warning("[7dtd-fastconnect] spawn hb failed (further failures muted):\n" + ex); }
                    catch { }
                }
            }
        }

        static void LogLoadGate(GameManager gm, World world)
        {
            bool started = gm.gameStateManager != null && gm.gameStateManager.IsGameStarted();
            int cgo = world.m_ChunkManager != null
                ? world.m_ChunkManager.GetDisplayedChunkGameObjectsCount() : -1;
            int viewDist = GameUtils.GetViewDistance();
            bool fixedSize = world.ChunkCache != null && world.ChunkCache.IsFixedSize;
            int needed = fixedSize ? 0 : viewDist * viewDist - 10;
            bool terrainReady = DistantTerrain.Instance == null || DistantTerrain.Instance.IsTerrainReady;
            var player = world.GetPrimaryPlayer();
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
        // position differs from Entity.serverPos by >= 2 on some axis.
        // Stats arrive at the server but position never does, so log both
        // sides of that comparison plus the spawn/remote flags feeding it.
        static void LogMovement(World world, EntityPlayer player, Vector3i b)
        {
            // serverPos is fixed-point in 1/32 block units (stock wire
            // scale), NOT blocks: a player at block -273 reads -8736
            // there. Compare in the same units or the delta is nonsense.
            // Convert with an arithmetic shift, which floors: plain '/'
            // truncates toward zero and reports raw -8752 as block -273
            // instead of -274, skewing delta/wouldSend by one block on
            // every negative-coordinate frame.
            Vector3i srvRaw = player.serverPos;
            Vector3i srv = new Vector3i(srvRaw.x >> 5, srvRaw.y >> 5, srvRaw.z >> 5);
            Vector3i d = b - srv;
            bool wouldSend = Mathf.Abs(d.x) >= 2 || Mathf.Abs(d.y) >= 2 || Mathf.Abs(d.z) >= 2;
            Log.Out("[7dtd-fastconnect] move hb posI=" + b + " serverPos=" + srv + " raw=" + srvRaw
                + " delta=" + d + " wouldSend=" + wouldSend
                + " spawned=" + player.IsSpawned() + " Spawned=" + player.Spawned
                + " remote=" + player.isEntityRemote
                + " attached=" + (player.AttachedToEntity != null)
                + " movementReplicated=" + player.IsMovementReplicated);
        }

        // The player free-falls from spawn to bedrock, so the client sees no
        // collision under it. Dump the client's own column around the player:
        // if these are air while the server says dirt/stone, our
        // NetPackageChunk content is the defect.
        static void LogBlockColumn(World world, Vector3i b)
        {
            string col = "";
            for (int dy = 2; dy >= -3; dy--)
            {
                var bv = world.GetBlock(b.x, b.y + dy, b.z);
                col += " y" + (b.y + dy) + "=" + bv.type;
            }
            // Blocks are correct yet the player falls through them, so
            // check the density channel: terrain collision comes from
            // marching-cubes density, not the block id. Stock encodes
            // air as sbyte +127 and full terrain as -128.
            string dens = "";
            for (int dy = 1; dy >= -2; dy--)
            {
                dens += " y" + (b.y + dy) + "=" + world.GetDensity(b.x, b.y + dy, b.z);
            }
            Log.Out("[7dtd-fastconnect] col hb at " + b.x + "," + b.z + col
                + " chunkLoaded=" + (world.GetChunkFromWorldPos(b) != null)
                + " dens:" + dens);
        }

        // Against stock the client gets colliders here; against zdtd
        // it gets none, so Origin's downward ray fails, the origin
        // thrashes every frame and RespawnProgress.WaitingForCollider
        // never clears. Log the chunk flags that gate collision-mesh
        // generation plus the same ray Origin casts (layer 0x10000).
        static void LogCollisionMesh(World world, EntityPlayer player, Vector3i b)
        {
            var ch = world.GetChunkFromWorldPos(b) as Chunk;
            bool hit = Physics.Raycast(
                new Ray(player.position + Vector3.up * 1.5f, Vector3.down),
                out RaycastHit rh, float.MaxValue, 0x10000);
            Log.Out("[7dtd-fastconnect] mesh hb chunk=" + (ch != null ? ch.Key.ToString() : "null")
                + (ch != null
                    ? " needsRegen=" + ch.NeedsRegeneration
                        + " needsLight=" + ch.NeedsLightCalculation
                        + " collGen=" + ch.IsCollisionMeshGenerated
                        + " onlyColl=" + ch.NeedsOnlyCollisionMesh
                        + " displayed=" + ch.IsDisplayed
                    : "")
                + " ray=" + hit + (hit ? " @" + rh.point + " " + rh.collider.name : ""));
        }

        // A chunk only meshes once its 8 neighbours are present, so a
        // single missing neighbour pins the player's chunk at
        // NeedsRegeneration forever. Dump the ring.
        static void LogNeighbourRing(World world, Vector3i b)
        {
            string ring = "";
            for (int dz = 4; dz >= -4; dz--)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    var n = world.GetChunkFromWorldPos(
                        new Vector3i(b.x + dx * 16, b.y, b.z + dz * 16)) as Chunk;
                    ring += n == null ? "." : (n.IsDisplayed ? "D" : (n.NeedsRegeneration ? "r" : "o"));
                }
                ring += "/";
            }
            Log.Out("[7dtd-fastconnect] ring hb " + ring + " (.=absent r=needsRegen o=meshed D=displayed)");
        }

        // World-content samples around the player: the availability ring
        // feeding ClampingToValidWorldPos, the abandoned_house_07 footprint,
        // and the biome/sky state driving terrain tint and fog.
        static void LogWorldSamples(World world, EntityPlayer player, Vector3i b)
        {
            // updateRespawn parks in ClampingToValidWorldPos, which means
            // World.IsPositionAvailable said no. That call needs the
            // player chunk and its 8 neighbours to exist AND report
            // GetAvailable(). Log both so the failing one is named.
            string avail = "";
            foreach (var dir in Vector3i.MIDDLE_AND_HORIZONTAL_DIRECTIONS_DIAGONAL)
            {
                var nc = world.ChunkCache.GetChunkFromWorldPos(b + dir * 16);
                avail += nc == null ? " null" : (nc.GetAvailable() ? " ok" : " NOTAVAIL");
            }
            // abandoned_house_07 sits at (-262,61,450) rotation 3 in
            // Navezgane's prefabs.xml. Dump its centre column so a
            // missing or vertically shifted POI is visible as data.
            // prefabs.xml positions are the prefab's origin CORNER, so probe
            // across the footprint of abandoned_house_07 (42x42 at
            // -262,61,450 rotation 3), not just the corner cell.
            string poi = "";
            int solid = 0;
            for (int ox = 2; ox <= 38; ox += 6)
            {
                for (int oz = 2; oz <= 38; oz += 6)
                {
                    int top = 0;
                    for (int y = 80; y >= 58; y--)
                    {
                        if (world.GetBlock(-262 + ox, y, 450 + oz).type != 0) { top = y; break; }
                    }
                    if (top > 61) solid++;
                }
            }
            for (int y = 72; y >= 58; y--) poi += " y" + y + "=" + world.GetBlock(-241, y, 471).type;
            // The whole scene renders grey/hazy versus the stock server, and
            // terrain tint plus biome fog are driven by the chunk's biome id,
            // so log what the client actually received.
            var pch = world.GetChunkFromWorldPos(b) as Chunk;
            Log.Out("[7dtd-fastconnect] biome hb chunkBiome="
                + (pch != null ? pch.GetBiomeId(b.x & 15, b.z & 15).ToString() : "null")
                + " worldBiome=" + (world.GetBiome(b.x, b.z) != null ? world.GetBiome(b.x, b.z).m_sBiomeName : "null")
                + " dayPercent=" + SkyManager.dayPercent
                + " indoorFog=" + SkyManager.indoorFogOn);

            Log.Out("[7dtd-fastconnect] poi hb centre(-241,471):" + poi
                + " columnsAboveGround=" + solid + "/49");

            Log.Out("[7dtd-fastconnect] avail hb posAvailable="
                + world.IsPositionAvailable(player.position) + " ring:" + avail);
        }

        // Is the delivered window centred on the player, or is the
        // client meshing a window centred somewhere else? Bound the
        // whole chunk cache and compare with the player's chunk.
        static void LogCacheWindow(World world, Vector3i b)
        {
            int minX = int.MaxValue, maxX = int.MinValue;
            int minZ = int.MaxValue, maxZ = int.MinValue, cnt = 0, disp = 0;
            foreach (var c2 in world.ChunkCache.GetChunkArrayCopySync())
            {
                cnt++;
                if (c2.IsDisplayed) disp++;
                if (c2.X < minX) minX = c2.X;
                if (c2.X > maxX) maxX = c2.X;
                if (c2.Z < minZ) minZ = c2.Z;
                if (c2.Z > maxZ) maxZ = c2.Z;
            }
            Log.Out("[7dtd-fastconnect] cache hb n=" + cnt + " displayed=" + disp
                + " x=[" + minX + ".." + maxX + "] z=[" + minZ + ".." + maxZ + "]"
                + " playerChunk=" + (b.x >> 4) + "," + (b.z >> 4));
        }

        // The world now loads, but the loading background stays up:
        // updateLoadState only closes the spawn window group when
        // bChooseSpawnPosition is false, otherwise it shows the
        // spawn-point map and returns. Log the inputs to that.
        // "Respawn almost done" never logs, so the respawn state
        // machine stalls before it closes the loading screen. Let
        // the game print which state it is sitting in.
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

        // Screenshots show the loading artwork while the window-ID checks
        // all report closed, so enumerate what is actually open instead of
        // guessing IDs. frameCount tells apart "no new frames rendered" from
        // "loading screen still up". openWindows lists only "toolTip", which
        // is not what an in-game client looks like, yet the screen shows
        // loading artwork. Ask every registered window whether it is
        // actually showing rather than trusting the open list.
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

        // External screen grabs of this client are unreliable: the
        // window may be unfocused, occluded, or not mapped at all,
        // so they show stale or missing frames. Capture the real
        // framebuffer from inside the game instead.
        static void TryCaptureScreenshot(EntityPlayer player)
        {
            if (!player.IsSpawned() || _shots >= 16) return;
            _shots++;
            // Same profile derivation as BlockIdDump: resolves to the Proton
            // user dir under wine and stays valid on a native client.
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile)) profile = ".";
            string p = Path.Combine(
                profile, "AppData", "Roaming", "7DaysToDie",
                "zdtd_shot_" + _shots + ".png");
            ScreenCapture.CaptureScreenshot(p);
            Log.Out("[7dtd-fastconnect] shot " + _shots + " -> " + p);
        }
    }
}
