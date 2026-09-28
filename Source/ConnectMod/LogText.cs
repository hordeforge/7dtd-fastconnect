using System;
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
        /// <summary>
        /// The prefix every line this mod logs carries, so a reader and the
        /// join harness (JOIN_SOFT_RE in scripts/one_shot_join.sh)
        /// can pick mod output out of a client log that also carries stock
        /// and LiteNetLib lines. One constant because a prefix spelled out at
        /// each call site is a marker that can drift.
        /// </summary>
        internal const string Tag = "[7dtd-fastconnect] ";

        /// <summary>
        /// Every Unicode format character (general category Cf), which a
        /// terminal renders as nothing (or lays the line out around) while the
        /// bytes stay in the log, so a reader sees a different string than
        /// grep does. char.IsControl does not cover them (they are Cf, not Cc),
        /// so each block is checked by range here.
        ///
        /// The set is spelled out rather than asked of
        /// CharUnicodeInfo.GetUnicodeCategory because the answer would come
        /// from the runtime's Unicode database, and the runtime this mod ships
        /// on is the Mono inside the game client, whose tables predate several
        /// of these blocks: a category query there passes the Trojan Source
        /// Egyptian format controls, the shorthand format controls and the
        /// whole U+E0001 tag block through untouched. The list is the whole of
        /// Cf as Unicode 15.1 defines it, which is what a current table
        /// returns, and scripts/log_sanitize.sh carries the same set.
        /// </summary>
        static bool IsInvisibleFormat(int cp)
        {
            return (cp >= 0x00AD && cp <= 0x00AD)   // soft hyphen
                || (cp >= 0x0600 && cp <= 0x0605)   // Arabic number signs
                || (cp >= 0x061C && cp <= 0x061C)   // Arabic letter mark
                || (cp >= 0x06DD && cp <= 0x06DD)   // Arabic end of ayah
                || (cp >= 0x070F && cp <= 0x070F)   // Syriac abbreviation mark
                || (cp >= 0x0890 && cp <= 0x0891)   // Arabic piastre/pound marks
                || (cp >= 0x08E2 && cp <= 0x08E2)   // Arabic disputed end of ayah
                || (cp >= 0x180E && cp <= 0x180E)   // Mongolian vowel separator
                || (cp >= 0x200B && cp <= 0x200F)   // ZWSP, ZWNJ, ZWJ, LRM, RLM
                || (cp >= 0x202A && cp <= 0x202E)   // LRE, RLE, PDF, LRO, RLO
                || (cp >= 0x2060 && cp <= 0x2064)   // word joiner, invisible operators
                || (cp >= 0x2066 && cp <= 0x206F)   // LRI, RLI, FSI, PDI, digit shapes
                || (cp >= 0xFEFF && cp <= 0xFEFF)   // BOM / zero-width no-break space
                || (cp >= 0xFFF9 && cp <= 0xFFFB)   // interlinear annotation
                || (cp >= 0x110BD && cp <= 0x110BD) // Kaithi number sign
                || (cp >= 0x110CD && cp <= 0x110CD) // Kaithi number sign above
                || (cp >= 0x13430 && cp <= 0x1343F) // Egyptian hieroglyph format controls
                || (cp >= 0x1BCA0 && cp <= 0x1BCA3) // shorthand format controls
                || (cp >= 0x1D173 && cp <= 0x1D17A) // musical format controls
                || (cp >= 0xE0001 && cp <= 0xE0001) // language tag
                || (cp >= 0xE0020 && cp <= 0xE007F); // tag characters
        }

        /// <summary>
        /// A code point that must not reach a log line verbatim: a control
        /// character (a newline forges a marker a harness greps for), a
        /// Unicode line/paragraph separator (a log reader lays it out as a
        /// line break though grep does not), or an invisible format character
        /// (renders as nothing, so the line reads as something it is not).
        /// The C0, DEL and C1 controls all live in the BMP, so the char
        /// overload answers for them and nothing else: a value above U+FFFF is
        /// an astral code point or an unpaired surrogate half, and neither
        /// char.IsControl nor a BMP range covers one.
        /// </summary>
        static bool IsLogUnsafe(int cp)
        {
            return (cp <= 0xFFFF && char.IsControl((char)cp))
                || IsInvisibleFormat(cp)
                || cp == 0x2028 || cp == 0x2029;
        }

        /// <summary>
        /// Flattens control, line-breaking and invisible-format characters so
        /// a launch-context string stays one readable log line. Env and argv
        /// values are attacker-shapable (a clicked steam://run URL chooses
        /// -connect= text), and join harnesses grep the client log for fixed
        /// markers; an embedded newline could forge those markers without
        /// ever connecting, a U+2028 breaks the line in a reader that grep
        /// reads as one, and a bidi override could render a forged line
        /// that reads differently from the text a grep sees. One code point
        /// becomes one space, so the UTF-16 length a downstream cap counts is
        /// preserved.
        ///
        /// The walk is by code point, not by char: the tag characters
        /// (U+E0020..U+E007F) and the Egyptian hieroglyph format controls are
        /// format characters above the BMP, and a char-at-a-time loop would
        /// test the two surrogate halves of each and find nothing.
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
            StringBuilder sb = null;
            int i = 0;
            while (i < value.Length)
            {
                int width = 1;
                int cp = value[i];
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length
                    && char.IsLowSurrogate(value[i + 1]))
                {
                    cp = char.ConvertToUtf32(value[i], value[i + 1]);
                    width = 2;
                }
                bool flatten = IsLogUnsafe(cp);
                if (sb == null)
                {
                    // Nothing unsafe yet, so the original string is still the
                    // answer; the copy starts only at the first flattened code
                    // point.
                    if (!flatten)
                    {
                        i += width;
                        continue;
                    }
                    sb = new StringBuilder(value.Length);
                    sb.Append(value, 0, i);
                }
                if (flatten) sb.Append(' ', width);
                else sb.Append(value, i, width);
                i += width;
            }
            return sb == null ? value : sb.ToString();
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
