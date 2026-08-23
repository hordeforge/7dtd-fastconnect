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
                return EnvFlags.IsSetOn(value);

            return ConnectTarget.TryFromLaunchContext(out _, out _, out _);
        }
    }
}
