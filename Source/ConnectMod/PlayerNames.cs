using System;

namespace SdtdConnect
{
    /// <summary>
    /// Client display-name resolution shared by the InitMod prefs override,
    /// the ClientInfo.playerName guard, and the prefs fallback: stock dedi
    /// kicks "Empty name or player ID" for loopback joins when Steam is
    /// offline, so every path must produce a non-empty name.
    /// </summary>
    internal static class PlayerNames
    {
        // Keep the resolved name inside the stock client-name length limit.
        internal const int MaxLength = 24;

        /// <summary>
        /// Environment user name, trimmed and length-capped; never empty.
        /// Falls back to machine name so two clients on different hosts never
        /// resolve to the same identity (the server rejects duplicates).
        /// </summary>
        internal static string Resolve()
        {
            string name = null;
            try { name = Environment.UserName; } catch { }
            if (string.IsNullOrWhiteSpace(name))
            {
                try { name = Environment.MachineName; } catch { }
            }
            if (string.IsNullOrWhiteSpace(name)) name = "player";
            name = name.Trim();
            if (name.Length > MaxLength) name = name.Substring(0, MaxLength);
            return name;
        }
    }
}
