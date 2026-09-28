using System;

namespace SdtdConnect
{
    /// <summary>
    /// Announce-once channel shared by the guarded diagnostic probes
    /// (boot/spawn/load/window/flags traces): silence is indistinguishable
    /// from a healthy quiet join, but a persistently dead probe also must not
    /// flood the client log that join harnesses grep for fixed markers. First
    /// failure announces; the rest stay muted.
    /// </summary>
    internal static class ProbeFailure
    {
        static bool _announced;

        internal static void Once(string what, Exception ex)
        {
            if (_announced || ex == null) return;
            _announced = true;
            // Swallows a failure of the game's own logger. This is the last
            // stop for every probe failure in the mod, so there is nowhere
            // left to report to; rethrowing would push a diagnostic's failure
            // into the stock call site the probe was only observing.
            try { Log.Warning("[7dtd-fastconnect] " + what + " failed:\n" + ex + " (further failures muted)"); }
            catch (Exception) { }
        }
    }
}
