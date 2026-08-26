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
            if (_announced || ex == null) return;
            Announce(what + " failed:\n" + ex);
        }

        // Reason-shaped variant for drift detected without an exception
        // (a reflection target that resolved to null): same once-and-mute
        // contract, so a renamed game member is named instead of degrading
        // silently.
        internal static void Once(string what, string reason)
        {
            if (_announced || string.IsNullOrEmpty(reason)) return;
            Announce(what + ": " + reason);
        }

        static void Announce(string body)
        {
            _announced = true;
            // Swallows a failure of the game's own logger. This is the last
            // stop for every probe failure in the mod, so there is nowhere
            // left to report to; rethrowing would push a diagnostic's failure
            // into the stock call site the probe was only observing.
            try { Log.Warning("[7dtd-fastconnect] " + body + " (further failures muted)"); }
            catch (Exception) { }
        }
    }
}
