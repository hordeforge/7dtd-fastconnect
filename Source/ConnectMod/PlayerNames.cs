using System;

namespace SdtdConnect
{
    /// <summary>
    /// Client display-name resolution, used by the InitMod prefs override and
    /// by its no-env fallback: stock dedi kicks "Empty name or player ID" for
    /// loopback joins when Steam is offline, so a stored PlayerName must
    /// never end up empty.
    /// </summary>
    internal static class PlayerNames
    {
        // Keep the resolved name inside the stock client-name length limit,
        // counted in code points (TextUtil): a name of emoji or CJK spends one
        // of its 24 characters per character, not two.
        internal const int MaxLength = 24;

        /// <summary>
        /// The one form a display name takes before it is stored: no control
        /// or invisible-format characters, trimmed, NFC, and capped at
        /// MaxLength code points without splitting a surrogate pair. The name
        /// reaches the server and lands in its logs and player list, so an env
        /// value carrying a newline or a bidi override would forge a line
        /// there exactly as it would in the client log (ConnectTarget.
        /// SanitizeForLog owns the character rule; the name is an identity
        /// string, not only a log value, so it is cleaned before it is stored
        /// rather than only before it is echoed). Returns null for a null or
        /// empty input, so the caller can treat a value that normalized away
        /// as the same signal as an absent one.
        /// </summary>
        internal static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            return Cap(ConnectTarget.SanitizeForLog(raw).Trim());
        }

        /// <summary>
        /// NFC, and capped at MaxLength code points without splitting a
        /// surrogate pair. The env override and the fallback both go through
        /// it, so an operator-supplied name cannot differ from a resolved one
        /// in encoding form, length unit, or truncation.
        /// </summary>
        internal static string Cap(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return TextUtil.TruncateToCodePoints(TextUtil.NormalizeFormC(name), MaxLength);
        }

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
            return Cap(name.Trim());
        }

        /// <summary>
        /// Trims a name to the stock cap without cutting a surrogate pair in
        /// half: a name is stored and echoed by the server, and a lone
        /// surrogate in it is not a valid name there. Dropping the whole
        /// astral character costs at most one code point of length.
        /// </summary>
        internal static string CapToMaxLength(string name)
        {
            if (name == null || name.Length <= MaxLength) return name;
            int cut = MaxLength;
            if (char.IsHighSurrogate(name[cut - 1])) cut--;
            return name.Substring(0, cut);
        }
    }
}
