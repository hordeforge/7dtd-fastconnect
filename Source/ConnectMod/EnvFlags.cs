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

        /// <summary>Opt-out flag: true only for 0/false/no/off (any case).</summary>
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

        /// <summary>True for blank or a documented boolean token in either direction.</summary>
        internal static bool IsKnownBool(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return true;
            return IsOptOut(raw) || IsRecognizedOn(raw);
        }

        static bool IsRecognizedOn(string raw)
        {
            string value = raw.Trim();
            return value == "1"
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Warns when a boolean var holds an undocumented token. Callers read
        /// their var once and snapshot it, so this fires once per variable.
        /// </summary>
        internal static void WarnUnknownValue(string name, string raw)
        {
            if (IsKnownBool(raw)) return;
            Log.Warning("[7dtd-fastconnect] " + name + "='" + ConnectTarget.SanitizeForLog(raw.Trim())
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
