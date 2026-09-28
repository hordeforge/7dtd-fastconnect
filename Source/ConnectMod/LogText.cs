using System.Text;

namespace SdtdConnect
{
    /// <summary>
    /// Log-line hygiene shared by every module that echoes operator input.
    /// Leaf helper: env parsing, connect targets, player names and console
    /// commands all depend on it, never the other way round. It owns the
    /// character rule outright; scripts/log_sanitize.sh is the shell twin.
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

        // char.IsControl covers C0, DEL and C1 but not the Unicode line and
        // paragraph separators, which a log reader lays out as a line break
        // even though grep does not: the same forged-marker shape, one layer
        // down.
        static bool IsLineBreaking(char c)
        {
            return char.IsControl(c) || c == '\u2028' || c == '\u2029';
        }

        // A character that must not reach a log line verbatim: a control or
        // line-breaking character (a newline forges a marker a harness greps
        // for) or an invisible format character (renders as nothing, so the
        // line reads as something it is not).
        static bool IsLogUnsafe(char c) => IsLineBreaking(c) || IsInvisibleFormat(c);

        /// <summary>
        /// Flattens control, line-breaking and invisible-format characters so
        /// a launch-context string stays one readable log line. Env and argv
        /// values are attacker-shapable (a clicked steam://run URL chooses
        /// -connect= text), and join harnesses grep the client log for fixed
        /// markers; an embedded newline could forge those markers without
        /// ever connecting, a U+2028 breaks the line in a reader that grep
        /// reads as one, and a bidi override could render a forged line
        /// that reads differently from the text a grep sees. One character
        /// becomes one space, so offsets and lengths are preserved.
        ///
        /// The single implementation of the rule: ConnectTarget, PlayerNames
        /// and EnvFlags delegate here, and scripts/log_sanitize.sh is the shell
        /// twin. It covers the U+2028/U+2029 separators a log reader lays out
        /// as a line break. The rule used to be duplicated: the copy inside
        /// ConnectTarget had already drifted, both ways, once keeping control
        /// characters only (so the -connect= warning paths echoed bidi
        /// overrides and a BOM straight into the client log) and then missing
        /// the two Unicode separators, so it is gone rather than kept in step
        /// by hand; and a per-module EchoForMessage cut the echo on UTF-16
        /// units, so a pasted astral character reached the log as a lone
        /// surrogate.
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
        /// scroll the reason off screen. The cut counts code points and never
        /// splits a surrogate pair (TextUtil), so the echo cannot put a lone
        /// surrogate in front of the operator.
        /// </summary>
        internal static string EchoForMessage(string value)
        {
            const int maxChars = 40;
            if (string.IsNullOrEmpty(value)) return value;
            string flat = SanitizeForLog(value).Trim();
            if (TextUtil.CodePointCount(flat) <= maxChars) return flat;
            return TextUtil.TruncateToCodePoints(flat, maxChars) + "...";
        }
    }
}
