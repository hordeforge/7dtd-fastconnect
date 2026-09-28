using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace SdtdConnect
{
    /// <summary>
    /// Local-host startup and in-world frame-hitch instrumentation, opt-in via
    /// 7DTD_CONNECT_DEBUG or `diag on`. Diagnostic only: nothing here changes
    /// startup, it just records what the local-host world-load workaround
    /// (LocalHostWorldLoad) was doing while the game was busy.
    /// </summary>
    static class PerfTrace
    {
        // Flatten completes once per local-host StartAsServer, so an
        // unconditional start would stack one more eternal coroutine on every
        // host session for the rest of the process. The monitor is meant to
        // run for the whole lifetime ("diag on" mid-session must still see
        // hitches), so keep exactly one instead of adding a stop path.
        static bool _hitchMonitorStarted;

        /// <summary>
        /// Whether a trace line would be emitted. Call sites on the startup
        /// coroutines must test this before building their line: the argument
        /// is concatenated by the caller, so gating inside Trace alone still
        /// pays for the string on every step of a world load that logs nothing.
        /// </summary>
        internal static bool Enabled => DiagToggle.Enabled;

        /// <summary>
        /// Local-host startup trace, `diag on` / 7DTD_CONNECT_DEBUG=1 only
        /// (~330 steps). Both local-host startup stages report through it, the
        /// createWorld step walk and the StartAsServer step walk, and each
        /// message names its stage.
        /// </summary>
        internal static void Trace(string message)
        {
            if (DiagToggle.Enabled)
                Log.Out("[7dtd-fastconnect] startup trace: " + message);
        }

        internal static void StartHitchMonitor()
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
                // The first throw would end the coroutine, and the start latch
                // (deliberate, so `diag on` mid-session keeps one monitor)
                // would keep StartHitchMonitor from ever restarting it, so the
                // session would report no hitches at all. Guard the body and
                // release the latch instead.
                try
                {
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
                        + " pendingLoads " + LocalHostWorldLoad.PendingLoadCount()
                        + " heap " + (GC.GetTotalMemory(false) >> BytesToMegabytesShift) + "MB"
                        + " targetFps " + Application.targetFrameRate
                        + " vsync " + QualitySettings.vSyncCount);
                    gc0 = n0; gc1 = n1; gc2 = n2;
                }
                catch (Exception ex)
                {
                    _hitchMonitorStarted = false;
                    Log.Warning("[7dtd-fastconnect] hitch monitor stopped: " + ex.GetType().Name + ": " + ex.Message);
                    yield break;
                }
            }
        }
    }
}
