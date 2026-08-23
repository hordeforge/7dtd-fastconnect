using System;

namespace SdtdConnect
{
    /// <summary>Runtime + persistent toggle for verbose 7dtd-connect traces.</summary>
    internal static class DiagToggle
    {
        // Env flip set at launch: 7DTD_CONNECT_DEBUG=1 / true
        static bool EnvEnabled
        {
            get
            {
                try
                {
                    return EnvFlags.IsSetOn(Environment.GetEnvironmentVariable("7DTD_CONNECT_DEBUG"));
                }
                catch { return false; }
            }
        }

        // Console toggle: F1 `diag on/off/toggle/status`
        static bool _consoleOverride;
        static bool _consoleHasOverride;
        static bool _reported;

        public static bool Enabled
        {
            get
            {
                if (_consoleHasOverride) return _consoleOverride;
                return EnvEnabled;
            }
        }

        /// <summary>Called on InitMod and on MainMenuOpened so reconnects see the env.</summary>
        internal static void AnnounceOnce()
        {
            if (_reported) return;
            _reported = true;
            if (Enabled) Log.Out("[7dtd-connect] diag verbose ON (7DTD_CONNECT_DEBUG=1 or `diag on`)");
        }

        // Console command sets this via DiagToggle.Set(consoleValue, interactive: true)
        internal static void Set(bool on)
        {
            _consoleHasOverride = true;
            _consoleOverride = on;
            _reported = false;
            try
            {
                // Also mirror into process env so fresh reads agree.
                Environment.SetEnvironmentVariable("7DTD_CONNECT_DEBUG", on ? "1" : "0");
            }
            catch { }
        }

        internal static string StatusLine()
        {
            string src = _consoleHasOverride ? "console" : (EnvEnabled ? "env" : "default");
            return "[7dtd-connect] diag " + (Enabled ? "ON" : "OFF") + " (" + src + ")";
        }
    }
}
