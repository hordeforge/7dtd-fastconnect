using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SdtdConnect
{
    /// <summary>
    /// A stock Local-platform host can stop resuming world-loading coroutines under
    /// Proton once the offline server starts its worker threads. Keep automation on
    /// its existing path, and scope the workaround to a normal local host.
    /// </summary>
    static class LocalHostWorldLoad
    {
        const int CreateWorldUnsafeFrameBreaks = 9;

        /// <summary>
        /// The workaround only runs during world load, and the stall it is being
        /// used to diagnose happens within the first few dozen steps. Trace that
        /// window unconditionally -- two runs were wasted producing empty logs
        /// because the trace was gated behind a flag nobody had turned on -- and
        /// cap it so a healthy load cannot spam the log. `diag on` lifts the cap.
        /// </summary>
        const int UngatedTraceSteps = 250;

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
            Log.Out("[7dtd-connect] Local-host world-load workaround active");

            // Stock leaves backgroundLoadingPriority at Low, which caps how much
            // time Unity gives async loads per frame. Draining World.LoadWorld
            // synchronously monopolises the main thread on top of that, so the
            // async waits later in createWorld (Resources.LoadAsync for
            // WeatherManager, LoadManager.LoadAsset for SkySystem) can crawl or
            // appear to hang -- the same Proton starvation the automation path
            // already works around with ApplyFrameUncap. runInBackground matters
            // too: Unity throttles an unfocused window, and loading a world while
            // the player alt-tabs is ordinary. Scoped to the load and restored
            // after, so no user preference is changed.
            ThreadPriority previousLoadPriority = Application.backgroundLoadingPriority;
            bool previousRunInBackground = Application.runInBackground;
            try
            {
                Application.backgroundLoadingPriority = ThreadPriority.High;
                Application.runInBackground = true;
                Log.Out("[7dtd-connect] Local-host load priority raised (was "
                    + previousLoadPriority + ", runInBackground was " + previousRunInBackground + ")");
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-connect] Local-host load priority raise failed: " + ex.Message);
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
                        Trace(step, "completed depth " + stack.Count + " after step " + step);
                        continue;
                    }
                    if (current is IEnumerator nested)
                    {
                        stack.Push(nested);
                        continue;
                    }
                    step++;
                    // The known stall freezes here with no further output. Logging the
                    // frame counter on both sides of the yield separates the two
                    // possible causes: a step that never returns (last "->" has no
                    // matching "<-") versus Unity silently dropping the coroutine
                    // (matching "<-", then nothing).
                    Trace(step, "-> step " + step + " depth " + stack.Count
                        + " yield " + (current == null ? "null" : current.GetType().Name)
                        + " frame " + UnityEngine.Time.frameCount);
                    yield return current;
                    Trace(step, "<- step " + step + " frame " + UnityEngine.Time.frameCount);
                }
            }
            finally
            {
                try
                {
                    Application.backgroundLoadingPriority = previousLoadPriority;
                    Application.runInBackground = previousRunInBackground;
                }
                catch (Exception ex)
                {
                    Log.Warning("[7dtd-connect] Local-host load priority restore failed: " + ex.Message);
                }
            }
            Log.Out("[7dtd-connect] Local-host startup completed");
        }

        /// <summary>Bounded during world load; unbounded with 7DTD_CONNECT_DEBUG=1 or `diag on`.</summary>
        static void Trace(int step, string message)
        {
            if (DiagToggle.Enabled || step <= UngatedTraceSteps)
                Log.Out("[7dtd-connect] StartAsServer trace: " + message);
        }

        static IEnumerator DrainWorldLoad(IEnumerator root)
        {
            FieldInfo forceSync = typeof(LoadManager).GetField("forceLoadSync",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            bool canRestore = forceSync != null && forceSync.FieldType == typeof(bool);
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
            // The observed freeze at "AstarManager Init" sits inside the suppressed
            // window, whose yields never reach Flatten. Trace here as well, or the
            // instrument misses the failure it exists to catch.
            while (MoveNext("createWorld", root, out object current))
            {
                Trace(++step, "createWorld step " + step
                    + " skipped " + skippedFrameBreaks
                    + " yield " + (current == null ? "null" : current.GetType().Name)
                    + " frame " + UnityEngine.Time.frameCount);
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
                Log.Error("[7dtd-connect] Local-host load failed in " + stage + ": " + ex);
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
