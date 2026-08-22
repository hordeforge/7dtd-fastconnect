using System;

namespace SdtdConnect
{
    [AttributeUsage(AttributeTargets.Class)]
    sealed class AutomationPatchAttribute : Attribute
    {
    }

    static class AutomationMode
    {
        internal const string EnvVar = "7DTD_CONNECT_AUTOMATION";
        static readonly bool _enabled = Detect();

        internal static bool Enabled => _enabled;

        static bool Detect()
        {
            string value = Environment.GetEnvironmentVariable(EnvVar);
            if (!string.IsNullOrWhiteSpace(value))
            {
                value = value.Trim();
                return value != "0"
                    && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
            }

            return ConnectTarget.TryFromLaunchContext(out _, out _, out _);
        }
    }
}
