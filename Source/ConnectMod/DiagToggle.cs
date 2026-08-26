namespace SdtdConnect
{
    /// <summary>Runtime + persistent toggle for verbose 7dtd-fastconnect traces.</summary>
    internal static class DiagToggle
    {
        internal const string EnvVar = "7DTD_CONNECT_DEBUG";

        // Shared cadence for every gated heartbeat (boot / spawn / load). One
        // value so the probes stay comparable in a single log: staggered
        // intervals make two heartbeats describing the same stall look like
        // different stalls.
        internal const float HeartbeatIntervalSec = 5f;

        // Snapshot once: Enabled sits first in per-frame/per-package hooks, and
        // a getenv there costs a native call plus a string alloc every frame.
        // The process env never changes at runtime; live toggling is Set().
        static readonly bool _envEnabled = EnvFlags.VarIsSetOn(EnvVar);

        // Console toggle: F1 `diag on/off/toggle/status`
        static bool _consoleOverride;
        static bool _consoleHasOverride;
        static bool _reported;

        public static bool Enabled
        {
            get
            {
                if (_consoleHasOverride) return _consoleOverride;
                return _envEnabled;
            }
        }

        /// <summary>Called on InitMod and on MainMenuOpened so reconnects see the env.</summary>
        internal static void AnnounceOnce()
        {
            if (_reported) return;
            _reported = true;
            if (Enabled) Log.Out("[7dtd-fastconnect] diag verbose ON (7DTD_CONNECT_DEBUG=1 or `diag on`)");
        }

        // Console command sets this; clearing _reported makes the next
        // AnnounceOnce() (InitMod / MainMenuOpened) re-log the flipped state.
        internal static void Set(bool on)
        {
            _consoleHasOverride = true;
            _consoleOverride = on;
            _reported = false;
        }

        internal static string StatusLine()
        {
            string src = _consoleHasOverride ? "console" : (_envEnabled ? "env" : "default");
            return "[7dtd-fastconnect] diag " + (Enabled ? "ON" : "OFF") + " (" + src + ")";
        }
    }
}
