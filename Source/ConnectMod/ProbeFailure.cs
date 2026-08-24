using System;

namespace SdtdConnect
{
    /// <summary>
    /// Announce-once channel shared by the guarded diagnostic probes
    /// (boot/spawn/load heartbeats and the BotTabPatch sections): silence is
    /// indistinguishable from a healthy quiet join, but a persistently dead
    /// probe also must not flood the client log that join harnesses grep for
    /// fixed markers. First failure announces; the rest stay muted.
    /// </summary>
    internal static class ProbeFailure
    {
        static bool _announced;

        internal static void Once(string what, Exception ex)
        {
            if (_announced) return;
            _announced = true;
            try { Log.Warning("[7dtd-fastconnect] " + what + " failed (further failures muted):\n" + ex); }
            catch { }
        }
    }
}
