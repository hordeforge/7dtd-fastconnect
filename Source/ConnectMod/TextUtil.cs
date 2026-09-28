using System;
using System.Globalization;
using System.Text;

namespace SdtdConnect
{
    /// <summary>
    /// Text rules for values that get a length cap or become an identity.
    /// Every cap in this mod is counted in code points, never in UTF-16 code
    /// units, and never inside a grapheme cluster, and every name is put in
    /// one normalization form before it is stored or compared.
    /// </summary>
    internal static class TextUtil
    {
        /// <summary>
        /// NFC, so the NFD spelling a macOS filesystem hands back and the NFC
        /// spelling the same account has elsewhere are one string to anything
        /// that dedupes it (the server rejects duplicate player names). A value
        /// .NET refuses to normalize (an unpaired surrogate from hand-built
        /// UTF-16) is returned unchanged rather than dropped: the cap still
        /// applies to it, so no input is lost.
        /// </summary>
        internal static string NormalizeFormC(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            try { return value.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { return value; }
        }

        /// <summary>
        /// Length in code points, which is the unit a character limit means:
        /// an emoji or a CJK ideograph is one character, and its UTF-16 pair
        /// is two units.
        /// </summary>
        internal static int CodePointCount(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            int count = 0;
            for (int i = 0; i < value.Length; i++)
            {
                count++;
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length
                    && char.IsLowSurrogate(value[i + 1]))
                {
                    i++;
                }
            }
            return count;
        }

        /// <summary>
        /// Cuts to <paramref name="maxCodePoints"/> code points, never inside a
        /// surrogate pair and never inside a grapheme cluster. A split pair is a
        /// lone surrogate, which the UTF-8 write in the prefs store and the wire
        /// encoder both turn into U+FFFD, so the player would join under a name
        /// that is not the one that was capped; a split cluster is a name that
        /// renders as a box: a dangling ZWJ at the end of an emoji family, or a
        /// combining mark with nothing to attach to. Both reach the server's
        /// player list, where there is nothing to explain them.
        /// </summary>
        internal static string TruncateToCodePoints(string value, int maxCodePoints)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (CodePointCount(value) <= maxCodePoints) return value;
            int i = 0;
            int count = 0;
            while (i < value.Length && count < maxCodePoints)
            {
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length
                    && char.IsLowSurrogate(value[i + 1]))
                {
                    i += 2;
                }
                else
                {
                    i++;
                }
                count++;
            }
            return TrimDanglingCluster(value, i);
        }

        // The cluster rule, kept beside the cut that has to honour it. A
        // combining mark and a ZWJ are only ever continued, never start a
        // cluster, so a cut that leaves one at the end has split the cluster it
        // belongs to and the whole remainder of that cluster goes with it.
        // Regional indicators pair up into a flag, so an odd run of them at the
        // end is half a flag. Nothing here splits a cluster that is already
        // whole, and the code points dropped are a handful of the cap's
        // budget, never enough to lose a name the cap would have kept whole.
        static string TrimDanglingCluster(string value, int end)
        {
            bool trimmed = true;
            while (trimmed && end > 0)
            {
                trimmed = false;
                // A cut that ends on a high surrogate is a lone one (its
                // partner would have been the next code point), whether the
                // count or a cluster trim put it there. Malformed input can
                // carry that pair; a cap must not be what hands it to an
                // encoder.
                if (char.IsHighSurrogate(value[end - 1]))
                {
                    end--;
                    trimmed = true;
                    continue;
                }
                int start = CodePointStart(value, end);
                int cp = CodePointBetween(value, start, end);
                if (cp == ZeroWidthJoiner || IsCombiningMark(cp) || IsEmojiModifier(cp))
                {
                    end = start;
                    trimmed = true;
                    continue;
                }
                int indicators = CountTrailingRegionalIndicators(value, end);
                if (indicators % 2 == 1)
                {
                    // The odd half is the last one before the cut, which is
                    // exactly the code point start already resolved above.
                    end = start;
                    trimmed = true;
                }
            }
            return value.Substring(0, end);
        }

        // U+200D, the joiner that chains emoji into one cluster. Not a
        // combining mark by category, but the same rule applies: it extends
        // the cluster before it and stands for nothing on its own.
        const int ZeroWidthJoiner = 0x200D;

        // U+1F3FB..U+1F3FF, the skin-tone modifiers. Symbol category rather
        // than mark, and the same rule: a modifier with no base renders as a
        // box.
        static bool IsEmojiModifier(int cp) => cp >= 0x1F3FB && cp <= 0x1F3FF;

        // The flag letters, U+1F1E6..U+1F1FF, which a client font pairs
        // two-by-two into one regional-indicator symbol.
        static bool IsRegionalIndicator(int cp) => cp >= 0x1F1E6 && cp <= 0x1F1FF;

        // A mark is a single BMP character: every combining mark Unicode
        // defines is below U+FFFF, so the category lookup stays on the char
        // overload and an astral code point is answered by the two range tests
        // above rather than by a surrogate half's category (Surrogate).
        static bool IsCombiningMark(int cp)
        {
            if (cp > 0xFFFF) return false;
            switch (CharUnicodeInfo.GetUnicodeCategory((char)cp))
            {
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.EnclosingMark:
                    return true;
                default:
                    return false;
            }
        }

        static int CountTrailingRegionalIndicators(string value, int end)
        {
            int n = 0;
            while (end > 0)
            {
                int start = CodePointStart(value, end);
                if (!IsRegionalIndicator(CodePointBetween(value, start, end))) break;
                end = start;
                n++;
            }
            return n;
        }

        // Where the code point ending at <paramref name="end"/> begins. An
        // unpaired surrogate is its own code point: ConvertToUtf32 would
        // throw, and a cap has to walk past malformed input rather than stop
        // on it.
        static int CodePointStart(string value, int end)
        {
            return end >= 2 && char.IsLowSurrogate(value[end - 1])
                && char.IsHighSurrogate(value[end - 2])
                ? end - 2
                : end - 1;
        }

        static int CodePointBetween(string value, int start, int end)
        {
            return end - start == 2
                ? char.ConvertToUtf32(value[start], value[start + 1])
                : value[start];
        }
    }
}
