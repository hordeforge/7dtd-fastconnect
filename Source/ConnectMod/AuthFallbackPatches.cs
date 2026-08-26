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
            catch (Exception ex)
            {
                // Same empty-ticket fallback, but name the cause: a wedged
                // Steamworks init must not look like plain "no Steam".
                Log.Warning("[7dtd-fastconnect] steam GetAuthTicket probe failed (" + ex.GetType().Name + " " + ex.Message + "), returning empty ticket");
                __result = "";
                return false;
            }
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
        // Real individual SteamID64s occupy [76561197960265728, +2^32): the
        // account number is a uint32, so the highest real id is
        // 76561202255233023. Synthetic ids start above that ceiling, so a
        // derived id can never collide with a real player's account (the
        // server would otherwise merge or kick two people onto one persisted
        // identity).
        const ulong SyntheticIdBase = 80000000000000000UL;
        // Keeps every derived id inside the synthetic band.
        const ulong SyntheticIdSpan = 100000000UL;
        // Used when the OS names neither the host nor the user: any two such
        // clients do collide, which is the honest outcome when there is
        // nothing left to distinguish them by.
        const ulong SyntheticIdSeedless = SyntheticIdBase + 42UL;

        // FNV-1a 64-bit, chosen for being deterministic and dependency-free;
        // no security property is claimed or needed here.
        const ulong Fnv1aOffsetBasis = 14695981039346656037UL;
        const ulong Fnv1aPrime = 1099511628211UL;

        static PlatformUserIdentifierAbs _fake;

        // Deterministic per host: stable across restarts on the same machine
        // (server-side player data persists), distinct across machines.
        static PlatformUserIdentifierAbs SyntheticId()
        {
            // Either lookup throws when the OS cannot name a host or profile
            // (observed under a bare Proton prefix). A throw is the same
            // signal as an empty name here, and the next fallback covers it.
            string seed = null;
            try { seed = Environment.MachineName; } catch (Exception) { }
            if (string.IsNullOrWhiteSpace(seed))
            {
                try { seed = Environment.UserName; } catch (Exception) { }
            }
            if (string.IsNullOrWhiteSpace(seed))
                return new Platform.Steam.UserIdentifierSteam(SyntheticIdSeedless.ToString());
            ulong hash = Fnv1aOffsetBasis;
            foreach (char c in seed.Trim())
            {
                hash ^= c;
                hash *= Fnv1aPrime;
            }
            return new Platform.Steam.UserIdentifierSteam(
                (SyntheticIdBase + hash % SyntheticIdSpan).ToString());
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
            catch (Exception ex)
            {
                // Same naming rule as the auth-ticket prefix: a wedged
                // Steamworks init must not look like plain "no Steam";
                // here it also silently swaps the real platform id for the
                // synthetic one, which changes the server-side identity.
                ProbeFailure.Once("Steam identity probe", ex);
            }
            try
            {
                if (_fake == null) _fake = SyntheticId();
                __result = _fake;
                return false;
            }
            catch (Exception ex)
            {
                // This getter is polled at connect-ready cadence (~10 Hz): a
                // persistently failing synthetic id would silently flip every
                // poll back to the original getter (and, past the Finalizer,
                // to a null id) with no trace. Announce once.
                ProbeFailure.Once("synthetic steam id create", ex);
            }
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
                catch (Exception ex)
                {
                    // Both failures swallowed here leave callers with a null
                    // id (same state as pre-login) and no trace of why;
                    // announce once so identity drift is debuggable.
                    ProbeFailure.Once("synthetic steam id finalizer", ex);
                }
                return null;
            }
            return null;
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
            catch (Exception ex)
            {
                // Same naming rule as the Steam ticket prefix: a wedged
                // platform probe must not look like plain "not logged in",
                // because both end in the same empty ticket.
                Log.Warning("[7dtd-fastconnect] EOS auth-ticket login probe failed ("
                    + ex.GetType().Name + " " + ex.Message + "), returning empty ticket");
            }
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
