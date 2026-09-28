using System;

namespace SdtdConnect
{
    /// <summary>
    /// Gates auto-join until stock platform networking can SetupProtocols without NRE.
    /// NativePlatform null → HasNetworkingEnabled NRE before LiteNet Connect log.
    /// </summary>
    public static class ConnectReady
    {
        // Monotonic (unscaled) time when the cross user was first seen without an id.
        static float _crossWaitStart = -1f;

        // IsReady sits in a 10 Hz poll loop; log each expiry note once so a
        // permanently missing identity cannot flood the client log that join
        // harnesses grep for fixed markers. The cross note is cleared when an
        // episode ends, so a reconnect can announce again; the native note
        // has no reset and fires at most once per process.
        static bool _crossProceedLogged;
        static bool _nativeProceedLogged;

        /// <summary>
        /// True once stock platform networking can SetupProtocols without NRE.
        /// On false, <paramref name="reason"/> carries the gate that is still
        /// closed (a stable slug such as staticData=false,
        /// already-connected or NativePlatform=null, or a cross-user wait note
        /// with its deadline), and on a throw it carries the exception. Callers
        /// log that string, so the vocabulary is part of what join harnesses
        /// grep for.
        /// </summary>
        public static bool IsReady(out string reason)
        {
            reason = null;
            try
            {
                if (GameManager.Instance == null || !GameManager.Instance.bStaticDataLoaded)
                {
                    reason = "staticData=false";
                    return false;
                }

                var cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
                if (cm == null)
                {
                    reason = "ConnectionManager=null";
                    return false;
                }
                if (cm.IsConnected)
                {
                    // This gate is the one poller of the connection state, so
                    // it is where a connect request that was made is seen to
                    // have landed; the request latch releases on that.
                    ConnectTarget.NoteConnected();
                    reason = "already-connected";
                    return false;
                }

                // ProtocolManager.SetupProtocols: NativePlatform.HasNetworkingEnabled
                var native = Platform.PlatformManager.NativePlatform;
                if (native == null)
                {
                    reason = "NativePlatform=null";
                    return false;
                }

                if (!TryCrossUserReady(out string crossReason))
                {
                    reason = crossReason;
                    return false;
                }

                // Native steam user is optional when EAC off: block only during the
                // early boot window, then proceed unauthenticated (stock accepts that
                // on LiteNet when EAC off).
                //
                // This window is measured from process start, not from the first
                // null-id sighting the way the cross-user wait above is measured.
                // That asymmetry is deliberate: the native platform is expected to
                // have its identity from boot, so a gate that first runs late has
                // already seen the window close and there is nothing left to wait
                // for. Re-arming per episode would instead add 16 s of blocking to
                // a Steam-less Proton client, the case this mod mostly runs on,
                // where the id never arrives at all.
                const float nativeUserBootWindowSec = 16f;
                try
                {
                    var nUser = native.User;
                    if (nUser != null && nUser.PlatformUserId == null)
                    {
                        if (UnityEngine.Time.unscaledTime < nativeUserBootWindowSec)
                        {
                            reason = "Native.User.PlatformUserId=null (early; retry in a moment)";
                            return false;
                        }
                        if (!_nativeProceedLogged)
                        {
                            _nativeProceedLogged = true;
                            Log.Warning("[7dtd-fastconnect] note: Native.User.PlatformUserId=null past boot window, proceeding anyway");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Same poll-rate contract as the cross-user probe above.
                    ProbeFailure.Once("native-user probe", ex);
                }

                if (!PermissionsManager.IsMultiplayerAllowed())
                {
                    reason = "IsMultiplayerAllowed=false";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                // The reason is echoed by the caller's throttled wait line, so
                // this stays one short string; the type name goes in because
                // the message alone cannot tell a torn-down singleton from a
                // null reference.
                reason = "IsReady ex: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // EOS login must finish before connecting on Steam clients:
        // ProtocolManager.SetupProtocols builds Platform.EOS.NetworkServerEos
        // and NREs when the cross user has no id yet (observed racing the
        // [EOS] Login at ~8 s of boot). Wait for the cross user (bounded),
        // then proceed anyway so a broken or absent EOS login cannot block
        // the join forever. Local-mode clients have no cross platform, so
        // this gate never engages there.
        static bool TryCrossUserReady(out string reason)
        {
            reason = null;
            const float crossUserWaitMaxSec = 30f;
            try
            {
                var cross = Platform.PlatformManager.CrossplatformPlatform;
                // A null platform is a Local-mode client, or a cross platform
                // torn down before this check ran: same reset, same reason.
                var user = cross?.User;
                if (user == null || user.PlatformUserId != null)
                {
                    // The wait is only armed while the id is actually missing,
                    // so anything else (logged in, or the platform/user torn
                    // down) ends the episode: a deadline from an earlier
                    // episode kept across the gap would already be past, and
                    // the next wait would skip its whole window and join into
                    // the NRE it exists to avoid.
                    _crossWaitStart = -1f;
                    _crossProceedLogged = false;
                    return true;
                }

                if (_crossWaitStart < 0f)
                    _crossWaitStart = UnityEngine.Time.unscaledTime;
                if (UnityEngine.Time.unscaledTime - _crossWaitStart < crossUserWaitMaxSec)
                {
                    reason = "cross user not logged in yet";
                    return false;
                }
                if (!_crossProceedLogged)
                {
                    _crossProceedLogged = true;
                    Log.Out("[7dtd-fastconnect] note: Crossplatform.User.PlatformUserId=null past wait window, proceeding anyway");
                }
                return true;
            }
            catch (Exception ex)
            {
                // Same poll-rate contract as the native-user probe below.
                ProbeFailure.Once("cross-user probe", ex);
                return true;
            }
        }
    }
}
