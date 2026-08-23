using System;
using HarmonyLib;

namespace SdtdConnect
{
    /// <summary>Steam-less Proton: GetAuthTicket throws InvalidOperationException when Steamworks not init; return empty ticket so SendLogin succeeds on EAC-off LAN.</summary>
    [AutomationPatch]
    [HarmonyPatch(typeof(Platform.Steam.AuthenticationClient), nameof(Platform.Steam.AuthenticationClient.GetAuthTicket))]
    static class Patch_SteamAuthTicket_Steamless
    {
        static bool Prefix(Platform.Steam.AuthenticationClient __instance, ref string __result)
        {
            // Pre-check: avoid calling SteamAPI if not running; the original's SteamUser.GetAuthSessionTicket throws InvalidOperationException.
            try
            {
                if (!Steamworks.SteamAPI.IsSteamRunning())
                {
                    __result = "";
                    Log.Out("[7dtd-fastconnect] steam GetAuthTicket: no Steam, returning empty (EAC off LAN)");
                    return false;
                }
            }
            catch { __result = ""; return false; }
            return true;
        }
        // If original still throws, catch via Finalizer and supply empty result so SendLogin continues.
        static Exception Finalizer(Exception __exception, ref string __result)
        {
            if (__exception != null)
            {
                Log.Warning("[7dtd-fastconnect] GetAuthTicket Finalizer: " + __exception.GetType().Name + " " + __exception.Message + " -> empty ticket");
                __result = "";
                return null;
            }
            return null;
        }
    }

    /// <summary>Steam-less Proton: client has no Steam/EOS identity, but stock dedi's PlayerIdAuthorizer kicks Empty name or player ID. Inject a synthetic local Steam id (derived from the machine name, so two Steam-less clients on different hosts never collide) so SendLogin succeeds on EAC-off LAN (loopback). With a real Steam login present, pass through so the server sees the real id and validates the real ticket.</summary>
    [AutomationPatch]
    [HarmonyPatch(typeof(Platform.Steam.User), nameof(Platform.Steam.User.PlatformUserId), MethodType.Getter)]
    static class Patch_SteamUserId_Synthetic
    {
        // Individual-account SteamID64 base; keep derived ids inside the
        // standard universe and far below the bot-id range BotTabPatch uses.
        const ulong IndividualAccountBase = 76561197960265728UL;

        static PlatformUserIdentifierAbs _fake;

        // Deterministic per host: stable across restarts on the same machine
        // (server-side player data persists), distinct across machines.
        static PlatformUserIdentifierAbs SyntheticId()
        {
            string seed = null;
            try { seed = Environment.MachineName; } catch { }
            if (string.IsNullOrWhiteSpace(seed))
            {
                try { seed = Environment.UserName; } catch { }
            }
            if (string.IsNullOrWhiteSpace(seed))
                return new Platform.Steam.UserIdentifierSteam("76561199000000042");
            ulong hash = 14695981039346656037UL;
            foreach (char c in seed.Trim())
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return new Platform.Steam.UserIdentifierSteam(
                (IndividualAccountBase + hash % 100000000UL).ToString());
        }

        static bool Prefix(Platform.Steam.User __instance, ref PlatformUserIdentifierAbs __result)
        {
            // Real Steam identity available -> let the original getter return it.
            // Direct Steamworks call (avoids re-entering this patched getter).
            try
            {
                if (Steamworks.SteamAPI.IsSteamRunning())
                {
                    var sid = Steamworks.SteamUser.GetSteamID();
                    if (sid.IsValid() && sid.m_SteamID != 0UL)
                        return true;
                }
            }
            catch { }
            try
            {
                if (_fake == null) _fake = SyntheticId();
                __result = _fake;
                return false;
            }
            catch { }
            return true;
        }
        static Exception Finalizer(Exception __exception, ref PlatformUserIdentifierAbs __result)
        {
            if (__exception != null)
            {
                try
                {
                    if (_fake == null) _fake = SyntheticId();
                    __result = _fake;
                }
                catch { }
                return null;
            }
            return null;
        }
    }

    [AutomationPatch]
    [HarmonyPatch(typeof(ClientInfo), "playerName", MethodType.Getter)]
    static class Patch_ClientInfo_PlayerName_Guard
    {
        static void Postfix(ClientInfo __instance, ref string __result)
        {
            if (!string.IsNullOrWhiteSpace(__result)) return;
            try
            {
                // Prefer GamePrefs name, then synthetic fallback.
                string pref = null;
                try { pref = GamePrefs.GetString(EnumGamePrefs.PlayerName); } catch { }
                if (!string.IsNullOrWhiteSpace(pref)) { __result = pref.Trim(); return; }
            }
            catch { }
            __result = PlayerNames.Resolve();
        }
    }

    // EOS path: patch concrete type directly (interface dispatch fails IL). The NRE is at Platform.EOS.AuthClient.GetAuthTicket when EOS not logged in.
    [AutomationPatch]
    [HarmonyPatch(typeof(Platform.EOS.AuthClient), "GetAuthTicket")]
    static class Patch_EOSAuthTicket_NotLoggedIn
    {
        static bool Prefix(ref string __result)
        {
            // EOS login present -> let the original produce a real ticket so the
            // server can validate it; only short-circuit when EOS is not logged in.
            try
            {
                var cross = Platform.PlatformManager.CrossplatformPlatform;
                var user = cross != null ? cross.User : null;
                if (user != null && user.PlatformUserId != null)
                    return true;
            }
            catch { }
            __result = "";
            return false;
        }
        static Exception Finalizer(Exception __exception, ref string __result)
        {
            if (__exception != null)
            {
                Log.Warning("[7dtd-fastconnect] EOS GetAuthTicket Finalizer: " + __exception.GetType().Name + " " + __exception.Message + " -> empty");
                __result = "";
                return null;
            }
            return null;
        }
    }
}
