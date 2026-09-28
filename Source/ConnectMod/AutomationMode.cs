using System;

namespace SdtdConnect
{
    [AttributeUsage(AttributeTargets.Class)]
    sealed class AutomationPatchAttribute : Attribute
    {
    }

    /// <summary>
    /// Whether the automation boot patches run, decided once per process.
    /// 7DTD_CONNECT_AUTOMATION overrides it in either direction; with the
    /// variable unset it is on as soon as the launch context carries a
    /// parseable join target, so a runner that only sets 7DTD_CONNECT still
    /// gets the boot patches, and an ordinary client launch does not.
    /// </summary>
    static class AutomationMode
    {
        internal const string EnvVar = "7DTD_CONNECT_AUTOMATION";
        static readonly bool _enabled = Detect();

        internal static bool Enabled => _enabled;

        static bool Detect()
        {
            string value = EnvFlags.Read(EnvVar);
            if (!string.IsNullOrWhiteSpace(value))
            {
                EnvFlags.WarnUnknownValue(EnvVar, value);
                return EnvFlags.IsSetOn(value);
            }

            return ConnectTarget.TryFromLaunchContext(out _, out _, out _);
        }
    }
}
