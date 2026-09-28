using System;
using System.Text;

namespace SdtdConnect
{
    /// <summary>
    /// Text rules for values that get a length cap or become an identity.
    /// Every cap in this mod is counted in code points, never in UTF-16 code
    /// units, and every name is put in one normalization form before it is
    /// stored or compared.
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
        /// Cuts to <paramref name="maxCodePoints"/> code points and never
        /// inside a surrogate pair. A split pair is a lone surrogate, which the
        /// UTF-8 write in the prefs store and the wire encoder both turn into
        /// U+FFFD, so the player would join under a name that is not the one
        /// that was capped.
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
            // A cut that ends on a high surrogate is a lone one (its partner
            // would have been the next code point). Malformed input can carry
            // that pair; a cap must not be what hands it to an encoder.
            string cut = value.Substring(0, i);
            if (cut.Length > 0 && char.IsHighSurrogate(cut[cut.Length - 1]))
            {
                cut = cut.Substring(0, cut.Length - 1);
            }
            return cut;
        }
    }
}
