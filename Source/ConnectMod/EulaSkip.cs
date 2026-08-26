using System;

namespace SdtdConnect
{
    /// <summary>EULA acceptance shared by InitMod prefs and every skip patch.</summary>
    internal static class EulaSkip
    {
        /// <summary>The window name GUIWindowManager opens the EULA gate under.</summary>
        internal const string GateWindowName = "windowEula";

        // Recorded when the client has not published a EULA version yet
        // (prefs unwritten on a fresh profile). Any value the stock check
        // accepts as "at least the latest" works; this one stays above every
        // shipped EULA revision.
        const int AssumedEulaVersion = 99;

        /// <summary>Marks the latest EULA accepted and persists it. Returns the recorded version.</summary>
        internal static int AcceptLatest()
        {
            int latest = GamePrefs.GetInt(EnumGamePrefs.EulaLatestVersion);
            if (latest < 1) latest = AssumedEulaVersion;
            GamePrefs.Set(EnumGamePrefs.EulaLatestVersion, latest);
            GamePrefs.Set(EnumGamePrefs.EulaVersionAccepted, latest);
            GamePrefs.Instance?.Save();
            return latest;
        }

        /// <summary>
        /// Shared body for both GUIWindowManager.Open arities that can open
        /// GateWindowName: accept, reopen the main menu, and re-fire
        /// MainMenuOpened directly so auto-join fires even if the XUi path is
        /// gated. Once the window is the EULA gate it never falls back to
        /// stock Open. Callers match GateWindowName before calling, because
        /// GUIWindowManager.Open fires for every UI window per tick and the
        /// non-EULA path must stay allocation-free.
        /// </summary>
        internal static bool BlockGateWindow(GUIWindowManager wm, string logTag)
        {
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
