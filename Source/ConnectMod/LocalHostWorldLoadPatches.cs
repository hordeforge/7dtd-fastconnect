using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SdtdConnect
{
    /// <summary>
    /// A normal (non-automation) local host hangs on "Initializing world" under
    /// Proton. Stock builds the local player with ~20 synchronous addressable
    /// loads (SDCSUtils via EntityAlive.switchModelView), each ending in
    /// Addressables.WaitForCompletion(), which deadlocks while any async
    /// addressable operation is still in flight, and world creation leaves
    /// ~100 async loads queued in LoadManager. Automation never hits it because
    /// it forces every load sync from boot. Drain the async queue before player
    /// creation and hold sync loading until startup finishes.
    /// </summary>
    static class LocalHostWorldLoad
    {
        // createWorld yields exactly nine plain frame breaks before its first
        // async wait (four before World.LoadWorld, then one each after LoadWorld,
        // AstarManager, LootManager, LockManager, TraderManager). Those nine are
        // suppressed; every later yield (the WeatherManager LoadAsync loop, the
        // SkySystem WaitUntil, the rest) is passed through, because those must
        // reach Unity to complete. The sequence is unconditional code, so the
        // count is fixed per game build and does not vary with the save. Verify
        // it against GameManager.createWorld when the game updates. Replacing
        // this boundary with a throttle regressed at "AstarManager Init".
        const int CreateWorldUnsafeFrameBreaks = 9;

        internal static void WrapStartAsServer(ref IEnumerator result)
        {
            if (!IsNormalLocalHost() || result == null) return;
            result = Flatten(result);
        }

        internal static void WrapWorldLoad(ref IEnumerator result)
        {
            if (!IsNormalLocalHost() || result == null) return;
            result = DrainWorldLoad(result);
        }

        internal static void WrapCreateWorld(ref IEnumerator result)
        {
            if (!IsNormalLocalHost() || result == null) return;
            result = PrepareCreateWorld(result);
        }

        static bool IsNormalLocalHost()
            => !AutomationMode.Enabled
                && (SingletonMonoBehaviour<ConnectionManager>.Instance?.IsServer ?? false);

        static IEnumerator Flatten(IEnumerator root)
        {
            Log.Out("[7dtd-fastconnect] Local-host world-load workaround active");

            // One MoveNext of this child per frame: it only ever yields null
            // while waiting on the prefab, so forwarding preserves pacing.
            IEnumerator prewarm = PrewarmPlayerPrefab();
            while (prewarm.MoveNext()) yield return prewarm.Current;

            // Stock leaves backgroundLoadingPriority at Low, which caps how much
            // time Unity gives async loads per frame, and enables runInBackground
            // only in the editor, so loads crawl while the window is unfocused,
            // which is ordinary while a world loads. Draining World.LoadWorld
            // synchronously monopolises the main thread on top of that. Raised for the load
            // window and restored in the finally, so no user preference is
            // changed. Not BootUnblock.ApplyFrameUncap: that also drops vsync and
            // the frame cap, which are the player's settings outside automation.
            ThreadPriority previousLoadPriority = Application.backgroundLoadingPriority;
            bool previousRunInBackground = Application.runInBackground;
            try
            {
                Application.backgroundLoadingPriority = ThreadPriority.High;
                Application.runInBackground = true;
                Log.Out("[7dtd-fastconnect] Local-host load priority raised (was "
                    + previousLoadPriority + ", runInBackground was " + previousRunInBackground + ")");
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] Local-host load priority raise failed: " + ex.Message);
            }

            try
            {
                var stack = new Stack<IEnumerator>();
                stack.Push(root);
                int step = 0;
                while (stack.Count != 0)
                {
                    IEnumerator iterator = stack.Peek();
                    if (!MoveNext("StartAsServer", iterator, out object current))
                    {
                        stack.Pop();
                        Trace("completed depth " + stack.Count + " after step " + step);
                        continue;
                    }
                    if (current is IEnumerator nested)
                    {
                        stack.Push(nested);
                        continue;
                    }
                    step++;
                    // Frame counter on both sides of the yield separates a step that
                    // never returns (a "->" with no matching "<-") from Unity dropping
                    // the coroutine (matching "<-", then nothing).
                    Trace("-> step " + step + " depth " + stack.Count
                        + " yield " + (current == null ? "null" : current.GetType().Name)
                        + " frame " + Time.frameCount);
                    yield return current;
                    Trace("<- step " + step + " frame " + Time.frameCount);
                }
            }
            finally
            {
                ReleaseForceLoadSync();
                try
                {
                    Application.backgroundLoadingPriority = previousLoadPriority;
                    Application.runInBackground = previousRunInBackground;
                }
                catch (Exception ex)
                {
                    Log.Warning("[7dtd-fastconnect] Local-host load priority restore failed: " + ex.Message);
                }
            }
            Log.Out("[7dtd-fastconnect] Local-host startup completed");
            StartHitchMonitor();
        }

        // EntityFactory.CreateEntity loads Prefabs/prefabEntityPlayerLocal with
        // _loadSync:true. Start the same load asynchronously here and yield
        // until it is done, so the main thread keeps pumping; CreateEntity then
        // finds a completed handle. Does not fix the hang alone, shortens it.
        // Bounded like PrepareCreateWorld's drain below: a request that never
        // completes (renamed asset after a game update, wedged LoadManager)
        // must not hang startup silently past its budget. Falling through only
        // skips the head start; the held sync load still creates the player.
        static IEnumerator PrewarmPlayerPrefab()
        {
            LoadManager.AssetRequestTask<GameObject> playerPrefab;
            try
            {
                playerPrefab = LoadManager.LoadAsset<GameObject>(
                    "Prefabs/prefabEntityPlayerLocal", null, null, false, false);
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] Local-host player prefab prewarm failed: " + ex.Message);
                yield break;
            }

            Log.Out("[7dtd-fastconnect] Local-host prewarming local player prefab");
            const float prewarmMaxSec = 60f;
            float prewarmDeadline = Time.realtimeSinceStartup + prewarmMaxSec;
            while (!playerPrefab.IsDone)
            {
                if (Time.realtimeSinceStartup >= prewarmDeadline)
                {
                    Log.Warning("[7dtd-fastconnect] Local-host prefab prewarm timed out after "
                        + prewarmMaxSec + "s; continuing without it");
                    break;
                }
                yield return null;
            }
            if (playerPrefab.IsDone)
                Log.Out("[7dtd-fastconnect] Local-host local player prefab ready");
        }

        // Flatten completes once per local-host StartAsServer, so an
        // unconditional start would stack one more eternal coroutine on every
        // host session for the rest of the process. The monitor is meant to
        // run for the whole lifetime ("diag on" mid-session must still see
        // hitches), so keep exactly one instead of adding a stop path.
        static bool _hitchMonitorStarted;

        static void StartHitchMonitor()
        {
            if (_hitchMonitorStarted) return;
            _hitchMonitorStarted = true;
            ThreadManager.StartCoroutine(HitchMonitor());
        }

        // Frame time above which a frame is reported as a hitch. Well past any
        // ordinary frame at playable rates, so the log names stalls a player
        // would actually feel rather than jitter.
        const float HitchThresholdSec = 0.2f;
        // GC.GetTotalMemory returns bytes; report megabytes.
        const int BytesToMegabytesShift = 20;

        /// <summary>
        /// In-world frame-hitch attribution for the Local host, `diag on` only:
        /// every frame over HitchThresholdSec with GC deltas, LoadManager
        /// backlog and heap, plus the live frame cap / vsync, so a "GPU always
        /// busy, seconds-long hangs" report can be checked against what the
        /// renderer is told. The coroutine runs either way so `diag on`
        /// mid-session starts logging.
        /// </summary>
        static IEnumerator HitchMonitor()
        {
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            float last = Time.realtimeSinceStartup;
            bool announced = false;
            while (true)
            {
                yield return null;
                float now = Time.realtimeSinceStartup;
                float dt = now - last;
                last = now;
                if (dt < HitchThresholdSec || !DiagToggle.Enabled) continue;
                int n0 = GC.CollectionCount(0), n1 = GC.CollectionCount(1), n2 = GC.CollectionCount(2);
                if (!announced)
                {
                    announced = true;
                    Log.Out("[7dtd-fastconnect] hitch monitor: limitFpsPref "
                        + GamePrefs.GetInt(EnumGamePrefs.OptionsGfxLimitFpsInGame)
                        + " vsyncPref " + GamePrefs.GetInt(EnumGamePrefs.OptionsGfxVsync)
                        + " loadPriority " + Application.backgroundLoadingPriority);
                }
                Log.Out("[7dtd-fastconnect] hitch " + (int)(dt * 1000) + "ms frame " + Time.frameCount
                    + " gc +" + (n0 - gc0) + "/+" + (n1 - gc1) + "/+" + (n2 - gc2)
                    + " pendingLoads " + PendingLoadCount()
                    + " heap " + (GC.GetTotalMemory(false) >> BytesToMegabytesShift) + "MB"
                    + " targetFps " + Application.targetFrameRate
                    + " vsync " + QualitySettings.vSyncCount);
                gc0 = n0; gc1 = n1; gc2 = n2;
            }
        }

        /// <summary>Startup trace, `diag on` / 7DTD_CONNECT_DEBUG=1 only (~330 steps).</summary>
        static void Trace(string message)
        {
            if (DiagToggle.Enabled)
                Log.Out("[7dtd-fastconnect] StartAsServer trace: " + message);
        }

        static IEnumerator DrainWorldLoad(IEnumerator root)
        {
            FieldInfo forceSync = ForceSyncField();
            bool canRestore = forceSync != null;
            bool previousForceSync = canRestore && (bool)forceSync.GetValue(null);
            if (canRestore) forceSync.SetValue(null, true);

            try
            {
                var stack = new Stack<IEnumerator>();
                stack.Push(root);
                while (stack.Count != 0)
                {
                    IEnumerator iterator = stack.Peek();
                    if (!MoveNext("World.LoadWorld", iterator, out object current))
                    {
                        stack.Pop();
                        continue;
                    }
                    if (current is IEnumerator nested)
                        stack.Push(nested);
                    // Leaf yields in World.LoadWorld are frame-budget breaks or
                    // cleanup operations. Returning one triggers the Local-host stall.
                }
            }
            finally
            {
                if (canRestore) forceSync.SetValue(null, previousForceSync);
            }
            yield break;
        }

        static IEnumerator PrepareCreateWorld(IEnumerator root)
        {
            int skippedFrameBreaks = 0;
            int step = 0;
            // The suppressed window's yields never reach Flatten, so trace here too
            // or the instrument misses the failure it exists to catch.
            while (MoveNext("createWorld", root, out object current))
            {
                Trace("createWorld step " + (++step)
                    + " skipped " + skippedFrameBreaks
                    + " yield " + (current == null ? "null" : current.GetType().Name)
                    + " frame " + Time.frameCount);
                if (current is IEnumerator nested)
                {
                    if (skippedFrameBreaks >= CreateWorldUnsafeFrameBreaks)
                    {
                        yield return nested;
                        continue;
                    }

                    // The only child before the safe boundary is World.LoadWorld,
                    // whose wrapper drains it without exposing leaf yields.
                    while (MoveNext("createWorld child", nested, out object childCurrent))
                    {
                        if (childCurrent != null)
                            yield return childCurrent;
                    }
                    continue;
                }

                if (current == null && skippedFrameBreaks < CreateWorldUnsafeFrameBreaks)
                {
                    skippedFrameBreaks++;
                    continue;
                }
                yield return current;
            }

            // StartAsServer creates the local player next. Let the pending async
            // queue drain first; these nulls reach Unity, so LoadManager.Update
            // keeps pumping and the sync player-part loads then run against an
            // idle addressables system.
            // Same budget shape as Flatten's prewarm wait: a wedged LoadManager
            // must not hang startup silently past its cap.
            const float asyncDrainMaxSec = 60f;
            float deadline = Time.realtimeSinceStartup + asyncDrainMaxSec;
            int pending = PendingLoadCount();
            if (pending > 0) Log.Out("[7dtd-fastconnect] Local-host draining " + pending + " pending async loads before player creation");
            while (pending > 0 && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                pending = PendingLoadCount();
            }
            // The queue is empty *now*; anything starting async after this point
            // reopens the WaitForCompletion deadlock window. Force sync loading for
            // the rest of startup (released in Flatten's finally).
            HoldForceLoadSync();
            // A couple of extra frames so just-completed requests finish their callbacks.
            yield return null;
            yield return null;
            Log.Out(pending > 0
                ? "[7dtd-fastconnect] Local-host async drain timed out with " + pending + " pending"
                : "[7dtd-fastconnect] Local-host async loads drained, sync loading held until startup completes");
        }

        static bool _forceSyncHeld, _forceSyncPrevious;

        // Reflection target shared with BootUnblock.ForceLoadSyncField: one
        // lookup and one missing-field warning serve both the automation
        // set-once path and this hold/release wrapper, so the two cannot
        // disagree after a game update renames the field.
        static FieldInfo ForceSyncField() => BootUnblock.ForceLoadSyncField();

        static void HoldForceLoadSync()
        {
            try
            {
                FieldInfo field = ForceSyncField();
                if (field == null || _forceSyncHeld) return;
                _forceSyncPrevious = (bool)field.GetValue(null);
                field.SetValue(null, true);
                _forceSyncHeld = true;
            }
            catch (Exception ex) { Log.Warning("[7dtd-fastconnect] force-sync hold failed: " + ex.Message); }
        }

        static void ReleaseForceLoadSync()
        {
            if (!_forceSyncHeld) return;
            try { ForceSyncField().SetValue(null, _forceSyncPrevious); }
            catch (Exception ex) { Log.Warning("[7dtd-fastconnect] force-sync release failed: " + ex.Message); }
            _forceSyncHeld = false;
        }

        static FieldInfo _loadRequests, _deferredLoadRequests;
        static MethodInfo _workBatchCount;
        static bool _pendingResolved;

        static int PendingLoadCount()
        {
            try
            {
                if (!_pendingResolved)
                {
                    _pendingResolved = true;
                    const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    _loadRequests = typeof(LoadManager).GetField("loadRequests", flags);
                    _deferredLoadRequests = typeof(LoadManager).GetField("deferedLoadRequests", flags);
                    _workBatchCount = _loadRequests?.FieldType.GetMethod("Count", Type.EmptyTypes);
                }
                int count = 0;
                object batch = _loadRequests?.GetValue(null);
                if (batch != null && _workBatchCount != null) count += (int)_workBatchCount.Invoke(batch, null);
                if (_deferredLoadRequests?.GetValue(null) is ICollection deferred) count += deferred.Count;
                return count;
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] pending load count failed: " + ex.Message);
                return 0;
            }
        }

        static bool MoveNext(string stage, IEnumerator iterator, out object current)
        {
            try
            {
                if (!iterator.MoveNext())
                {
                    current = null;
                    return false;
                }
                current = iterator.Current;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[7dtd-fastconnect] Local-host load failed in " + stage + ": " + ex);
                throw;
            }
        }
    }

    [HarmonyPatch(typeof(World), nameof(World.LoadWorld))]
    static class Patch_LocalHost_WorldLoad
    {
        static void Postfix(ref IEnumerator __result) => LocalHostWorldLoad.WrapWorldLoad(ref __result);
    }

    [HarmonyPatch(typeof(GameManager), "createWorld")]
    static class Patch_LocalHost_CreateWorld
    {
        static void Postfix(ref IEnumerator __result) => LocalHostWorldLoad.WrapCreateWorld(ref __result);
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.StartAsServer))]
    static class Patch_LocalHost_StartAsServer
    {
        static void Postfix(ref IEnumerator __result) => LocalHostWorldLoad.WrapStartAsServer(ref __result);
    }
}
