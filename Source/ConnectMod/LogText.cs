using System.Text;

namespace SdtdConnect
{
    /// <summary>
    /// Log-line hygiene shared by every module that echoes operator input.
    /// Leaf helper: env parsing, connect targets and console commands all
    /// depend on it, never the other way round.
    /// </summary>
    internal static class LogText
    {
        // Unicode format characters a terminal renders as nothing (or as a
        // line reorder) but a reader or a grep of the text still sees: the
        // bidi overrides and embeddings, the LTR/RTR marks, the zero-width
        // space/joiner/non-joiner, and the BOM. char.IsControl does not cover
        // them (they are Cf, not Cc), so each is checked by range here.
        static bool IsInvisibleFormat(char c)
        {
            return (c >= '\u200B' && c <= '\u200F')   // ZWSP, ZWNJ, ZWJ, LRM, RLM
                || (c >= '\u2060' && c <= '\u2064')   // word joiner, invisible operators
                || (c >= '\u2066' && c <= '\u2069')   // LRI, RLI, FSI, PDI
                || (c >= '\u202A' && c <= '\u202E')   // LRE, RLE, PDF, LRO, RLO
                || c == '\uFEFF';                    // BOM / zero-width no-break space
        }

        // A character that must not reach a log line verbatim: a control
        // character (a newline forges a marker a harness greps for) or an
        // invisible format character (renders as nothing, so the line reads
        // as something it is not).
        static bool IsLogUnsafe(char c) => char.IsControl(c) || IsInvisibleFormat(c);

        /// <summary>
        /// Flattens control and invisible-format characters so a
        /// launch-context string stays one readable log line. Env and argv
        /// values are attacker-shapable (a clicked steam://run URL chooses
        /// -connect= text), and join harnesses grep the client log for fixed
        /// markers; an embedded newline could forge those markers without
        /// ever connecting, and a bidi override could render a forged line
        /// that reads differently from the text a grep sees. One character
        /// becomes one space, so offsets and lengths are preserved.
        ///
        /// Used by EnvFlags and the console echo path.
        /// ConnectTarget.SanitizeForLog is the stricter twin every
        /// launch-context value goes through: it keeps this character set and
        /// adds the U+2028/U+2029 separators a log reader lays out as a line
        /// break. scripts/log_sanitize.sh is the shell twin of that one.
        /// </summary>
        internal static string SanitizeForLog(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            bool dirty = false;
            foreach (char c in value)
            {
                if (IsLogUnsafe(c)) { dirty = true; break; }
            }
            if (!dirty) return value;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(IsLogUnsafe(c) ? ' ' : c);
            return sb.ToString();
        }

        /// <summary>
        /// One-line echo of operator input for an error message: log-unsafe
        /// characters flattened, long pastes cut so a mistyped paste cannot
        /// scroll the reason off screen.
        /// </summary>
        internal static string EchoForMessage(string value)
        {
            const int maxChars = 40;
            if (string.IsNullOrEmpty(value)) return value;
            string flat = SanitizeForLog(value).Trim();
            return flat.Length <= maxChars ? flat : flat.Substring(0, maxChars) + "...";
        }
    }
}
