using System;

namespace SdtdConnect
{
    /// <summary>
    /// Shared truthiness for boolean env overrides: unset/blank means the
    /// caller's default, 0/false/no/off (any case) opt out, anything else opts in.
    /// </summary>
    internal static class EnvFlags
    {
        internal static bool IsOptOut(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string value = raw.Trim();
            return value == "0"
                || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Opt-in flag: true when set to anything but an opt-out value.</summary>
        internal static bool IsSetOn(string raw)
        {
            return !string.IsNullOrWhiteSpace(raw) && !IsOptOut(raw);
        }
    }
}
