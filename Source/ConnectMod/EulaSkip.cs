using System;

namespace SdtdConnect
{
    /// <summary>EULA acceptance shared by InitMod prefs and every skip patch.</summary>
    internal static class EulaSkip
    {
        /// <summary>Marks the latest EULA accepted and persists it. Returns the recorded version.</summary>
        internal static int AcceptLatest()
        {
            int latest = GamePrefs.GetInt(EnumGamePrefs.EulaLatestVersion);
            if (latest < 1) latest = 99;
            GamePrefs.Set(EnumGamePrefs.EulaLatestVersion, latest);
            GamePrefs.Set(EnumGamePrefs.EulaVersionAccepted, latest);
            GamePrefs.Instance?.Save();
            return latest;
        }

        /// <summary>
        /// Shared body for both GUIWindowManager.Open arities that can open
        /// "windowEula": accept, reopen the main menu, and re-fire
        /// MainMenuOpened directly so auto-join fires even if the XUi path is gated.
        /// Once the window is windowEula it never falls back to stock Open.
        /// </summary>
        internal static bool BlockGateWindow(GUIWindowManager wm, string _windowName, string logTag)
        {
            if (_windowName != "windowEula") return true;
            try
            {
                Log.Out("[7dtd-fastconnect] blocking GUI " + logTag);
                AcceptLatest();
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] windowEula accept failed (" + logTag + "): " + ex.Message);
            }
            try
            {
                var xui = wm?.playerUI?.xui;
                if (xui != null) XUiC_MainMenu.Open(xui);
                var data = new ModEvents.SMainMenuOpenedData(true);
                ModEvents.MainMenuOpened.Invoke(ref data);
                Log.Out("[7dtd-fastconnect] dispatched MainMenuOpened after " + logTag);
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] MainMenuOpened dispatch failed (" + logTag + "): " + ex.Message);
            }
            return false;
        }
    }
}
