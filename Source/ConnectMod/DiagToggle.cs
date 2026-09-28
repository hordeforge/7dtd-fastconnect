namespace SdtdConnect
{
    /// <summary>
    /// Per-process toggle for verbose 7dtd-fastconnect traces: the env value is
    /// snapshotted once at type init and the F1 `diag` command overrides it for
    /// the rest of the process. Nothing is written to GamePrefs, so the state
    /// does not survive a restart.
    /// </summary>
    internal static class DiagToggle
    {
        internal const string EnvVar = "7DTD_CONNECT_DEBUG";

        // Shared cadence for the three join heartbeats (boot / spawn / load),
        // so the probes stay comparable in a single log: staggered intervals
        // make two heartbeats describing the same stall look like different
        // stalls. The boot heartbeat is not gated on DiagToggle (an
        // automation boot is a stall the harness is waiting out, so it logs
        // every run); spawn and load check the toggle before logging.
        internal const float HeartbeatIntervalSec = 5f;

        // Snapshot once: Enabled sits first in per-frame/per-package hooks, and
        // a getenv there costs a native call plus a string alloc every frame.
        // The process env never changes at runtime; live toggling is Set().
        static readonly bool _envEnabled = EnvFlags.VarIsSetOn(EnvVar);

        // Console toggle: F1 `diag on/off/toggle/status`. Null until the
        // command has been used, which is what lets the env snapshot stand.
        static bool? _consoleOverride;
        static bool _reported;

        public static bool Enabled => _consoleOverride ?? _envEnabled;

        /// <summary>
        /// Log the verbose-on notice once per process unless the console command
        /// clears the latch. The env snapshot is fixed at type init, so this
        /// cannot report a value that changed since InitMod.
        /// </summary>
        internal static void AnnounceOnce()
        {
            if (_reported) return;
            _reported = true;
            if (Enabled) Log.Out(LogText.Tag + "diag verbose ON (7DTD_CONNECT_DEBUG=1 or `diag on`)");
        }

        // Console command sets this; clearing _reported lets a later
        // AnnounceOnce() (InitMod / MainMenuOpened) log again, though only
        // while Enabled, so a `diag off` flip is not itself announced.
        internal static void Set(bool on)
        {
            _consoleOverride = on;
            _reported = false;
        }

        internal static string StatusLine()
        {
            string src = _consoleOverride.HasValue ? "console" : (_envEnabled ? "env" : "default");
            return LogText.Tag + "diag " + (Enabled ? "ON" : "OFF") + " (" + src + ")";
        }
    }
}
