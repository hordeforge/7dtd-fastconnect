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

        // IsReady sits in a 10 Hz poll loop; log each expiry note once per
        // episode so a permanently missing identity cannot flood the client
        // log that join harnesses grep for fixed markers.
        static bool _crossProceedLogged;
        static bool _nativeProceedLogged;

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

                // EOS login must finish before connecting on Steam clients:
                // ProtocolManager.SetupProtocols builds Platform.EOS.NetworkServerEos
                // and NREs when the cross user has no id yet (observed racing the
                // [EOS] Login at ~8 s of boot). Wait for the cross user (bounded),
                // then proceed anyway so a broken or absent EOS login cannot block
                // the join forever. Local-mode clients have no cross platform, so
                // this gate never engages there.
                const float crossUserWaitMaxSec = 30f;
                try
                {
                    var cross = Platform.PlatformManager.CrossplatformPlatform;
                    if (cross != null)
                    {
                        var user = cross.User;
                        if (user != null && user.PlatformUserId == null)
                        {
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
                        }
                        else if (user != null)
                        {
                            _crossWaitStart = -1f; // logged in; reset for later rejoins
                            _crossProceedLogged = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Out("[7dtd-fastconnect] cross-user note: " + ex.Message);
                }

                // Native steam user is optional when EAC off: block only during the
                // early boot window, then proceed unauthenticated (stock accepts that
                // on LiteNet when EAC off).
                const float nativeUserBootWindowSec = 16f;
                try
                {
                    var nUser = native.User;
                    if (nUser != null && nUser.PlatformUserId == null)
                    {
                        // GameManager.Instance was already verified non-null above.
                        if (UnityEngine.Time.unscaledTime < nativeUserBootWindowSec)
                        {
                            reason = "Native.User.PlatformUserId=null (early; retry in a moment)";
                            return false;
                        }
                        if (!_nativeProceedLogged)
                        {
                            _nativeProceedLogged = true;
                            Log.Out("[7dtd-fastconnect] note: Native.User.PlatformUserId=null past boot window, proceeding anyway");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Out("[7dtd-fastconnect] native-user note: " + ex.Message);
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
                reason = "IsReady ex: " + ex.Message;
                return false;
            }
        }
    }
}
