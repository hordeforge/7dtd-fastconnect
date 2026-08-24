using System;

namespace SdtdConnect
{
    /// <summary>
    /// Client-user directory derivation shared by the diagnostic writers
    /// (screenshots, block-id dump): under Proton this resolves to the guest
    /// user dir inside the prefix; when the OS cannot name a profile the
    /// working directory is used so a path is never composed from an empty
    /// prefix.
    /// </summary>
    internal static class UserDirs
    {
        internal static string ProfileDir()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(profile) ? "." : profile;
        }
    }
}
