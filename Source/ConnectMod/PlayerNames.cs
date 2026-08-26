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
            // Both lookups can throw when the OS cannot name a profile or host
            // (observed under a bare Proton prefix). Each failure is the same
            // signal as an empty value, and the next fallback covers it, so
            // there is nothing a caller could do with the exception.
            try { name = Environment.UserName; } catch (Exception) { }
            if (string.IsNullOrWhiteSpace(name))
            {
                try { name = Environment.MachineName; } catch (Exception) { }
            }
            if (string.IsNullOrWhiteSpace(name)) name = "player";
            name = name.Trim();
            if (name.Length > MaxLength) name = name.Substring(0, MaxLength);
            return name;
        }
    }
}
