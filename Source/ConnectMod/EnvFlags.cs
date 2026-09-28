using System;

namespace SdtdConnect
{
    /// <summary>
    /// Shared truthiness for boolean env overrides: unset/blank means the
    /// caller's default, 0/false/no/off (any case) opt out, anything else opts in.
    /// </summary>
    internal static class EnvFlags
    {
        /// <summary>One safe environment read: a blocked read yields null.</summary>
        internal static string Read(string name)
        {
            // A block on reading the environment (SecurityException under a
            // restricted host) carries no more information than an unset
            // variable, so every caller's default applies unchanged. Reading
            // raw here instead would let the exception escape into a mod-load
            // static initializer.
            try { return Environment.GetEnvironmentVariable(name); }
            catch (Exception) { return null; }
        }

        // The one token table, read in both directions. Every boolean env
        // override goes through Parse, so a token cannot be documented on the
        // opt-in side and missing on the opt-out side.
        static readonly string[] _optOutTokens = { "0", "false", "no", "off" };
        static readonly string[] _optInTokens = { "1", "true", "yes", "on" };

        /// <summary>
        /// The documented value of a boolean override: false for 0/false/no/off
        /// (any case), true for 1/true/yes/on, null for blank or for a token
        /// neither side documents.
        /// </summary>
        internal static bool? Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string value = raw.Trim();
            if (Array.Exists(_optOutTokens, t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase)))
                return false;
            if (Array.Exists(_optInTokens, t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase)))
                return true;
            return null;
        }

        /// <summary>Opt-out flag: true only for 0/false/no/off (any case).</summary>
        internal static bool IsOptOut(string raw) => Parse(raw) == false;

        /// <summary>Opt-in flag: true when set to anything but an opt-out value.</summary>
        internal static bool IsSetOn(string raw)
            => !string.IsNullOrWhiteSpace(raw) && Parse(raw) != false;

        /// <summary>
        /// Warns when a boolean var holds an undocumented token. Callers read
        /// their var once and snapshot it, so this fires once per variable.
        /// </summary>
        internal static void WarnUnknownValue(string name, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || Parse(raw) != null) return;
            Log.Warning("[7dtd-fastconnect] " + name + "='" + LogText.SanitizeForLog(raw.Trim())
                + "' is not a documented boolean (1/true/yes/on, or 0/false/no/off to disable); reading it as ON");
        }

        /// <summary>IsSetOn for an env var name; unreadable env counts as unset.</summary>
        internal static bool VarIsSetOn(string name)
        {
            string raw = Read(name);
            WarnUnknownValue(name, raw);
            return IsSetOn(raw);
        }

        /// <summary>IsOptOut for an env var name; unreadable env counts as unset.</summary>
        internal static bool VarIsOptOut(string name)
        {
            string raw = Read(name);
            WarnUnknownValue(name, raw);
            return IsOptOut(raw);
        }
    }
}
