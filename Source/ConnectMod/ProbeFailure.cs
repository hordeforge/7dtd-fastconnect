using System;
using System.Collections.Generic;

namespace SdtdConnect
{
    /// <summary>
    /// Announce-once channel for the guarded diagnostic probes (boot/spawn/load
    /// heartbeats, the window/spawn/flags traces, and the identity fallbacks):
    /// silence is indistinguishable from a healthy quiet join, but a
    /// persistently dead probe also must not flood the client log that join
    /// harnesses grep for fixed markers. The latch is keyed by probe name, so
    /// one dead probe cannot mute the announcement of another: a shared latch
    /// let a throwing heartbeat bury the synthetic-id notice, which is the one
    /// failure that silently changes the server-side player identity.
    /// </summary>
    internal static class ProbeFailure
    {
        // Keys are the literal `what` strings at the call sites (WindowTrace
        // adds its own "wt " hook name, so a handful per hook), never probe
        // input, so the set stays bounded by the number of probes.
        static readonly HashSet<string> _announced = new HashSet<string>(StringComparer.Ordinal);

        internal static void Once(string what, Exception ex)
        {
            if (ex == null) return;
            Announce(what, ex.ToString());
        }

        // Reason-shaped variant for a probe that has to report something other
        // than an exception. Same once-and-mute contract.
        internal static void Once(string what, string reason)
        {
            if (string.IsNullOrEmpty(reason)) return;
            Announce(what, reason);
        }

        static void Announce(string what, string detail)
        {
            if (!_announced.Add(what)) return;
            // Swallows a failure of the game's own logger. This is the last
            // stop for every probe failure in the mod, so there is nowhere
            // left to report to; rethrowing would push a diagnostic's failure
            // into the stock call site the probe was only observing.
            try { Log.Warning("[7dtd-fastconnect] " + what + " failed: " + detail + " (further failures muted)"); }
            catch (Exception) { }
        }
    }
}
