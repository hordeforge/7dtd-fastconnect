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
        /// Flattens control and invisible-format characters, trims, and caps
        /// the length. The name reaches the server and lands in its logs and
        /// player list, so an env value carrying a newline or a bidi override
        /// would forge a line there exactly as it would in the client log
        /// (ConnectTarget.SanitizeForLog owns the character rule; the name is
        /// an identity string, not only a log value, so it is cleaned before
        /// it is stored rather than only before it is echoed).
        /// </summary>
        internal static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string name = ConnectTarget.SanitizeForLog(raw).Trim();
            if (name.Length > MaxLength)
            {
                name = name.Substring(0, MaxLength);
                // A cap that lands between the halves of a surrogate pair
                // leaves an unpaired code unit that serializes as a
                // replacement character on the wire.
                if (char.IsHighSurrogate(name[name.Length - 1]))
                    name = name.Substring(0, name.Length - 1);
            }
            return name;
        }

        /// <summary>
        /// Environment user name, normalized; never empty.
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
            name = Normalize(name);
            return string.IsNullOrEmpty(name) ? "player" : name;
        }
    }
}
