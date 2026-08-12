using System;
using System.Net;

namespace ZdtdConnect
{
    /// <summary>Parse host:port from env / argv / console and drive stock ConnectionManager.Connect.</summary>
    public static class ConnectTarget
    {
        public const string EnvVar = "ZDTD_CONNECT";
        public const string EnvVarAlt = "7DTD_CONNECT";
        public const int DefaultPort = 27025;

        public static bool TryParse(string raw, out string host, out int port, out string error)
        {
            host = null;
            port = DefaultPort;
            error = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "empty target";
                return false;
            }

            raw = raw.Trim();
            // Accept host, host:port, or steam-style leftover without scheme.
            if (raw.StartsWith("steam://connect/", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring("steam://connect/".Length);

            string hostPart = raw;
            int portPart = DefaultPort;

            // IPv6 in brackets: [addr]:port
            if (raw.StartsWith("["))
            {
                int close = raw.IndexOf(']');
                if (close < 0)
                {
                    error = "bad IPv6 brackets";
                    return false;
                }
                hostPart = raw.Substring(1, close - 1);
                if (close + 1 < raw.Length && raw[close + 1] == ':')
                {
                    if (!int.TryParse(raw.Substring(close + 2), out portPart) || portPart < 1 || portPart > 65535)
                    {
                        error = "bad port";
                        return false;
                    }
                }
            }
            else
            {
                // Last colon separates port (IPv4 / hostname).
                int colon = raw.LastIndexOf(':');
                if (colon > 0 && colon < raw.Length - 1
                    && raw.IndexOf(':') == colon) // single colon → not bare IPv6
                {
                    hostPart = raw.Substring(0, colon);
                    if (!int.TryParse(raw.Substring(colon + 1), out portPart) || portPart < 1 || portPart > 65535)
                    {
                        error = "bad port";
                        return false;
                    }
                }
                else
                    hostPart = raw;
            }

            if (string.IsNullOrWhiteSpace(hostPart))
            {
                error = "empty host";
                return false;
            }

            host = hostPart.Trim();
            port = portPart;
            return true;
        }

        /// <summary>Env first (ZDTD_CONNECT, then 7DTD_CONNECT), then -connect= / +connect from argv.</summary>
        public static bool TryFromLaunchContext(out string host, out int port, out string source)
        {
            host = null;
            port = DefaultPort;
            source = null;

            foreach (var key in new[] { EnvVar, EnvVarAlt })
            {
                string env = Environment.GetEnvironmentVariable(key);
                if (string.IsNullOrWhiteSpace(env)) continue;
                if (TryParse(env, out host, out port, out _))
                {
                    source = key + "=" + env.Trim();
                    return true;
                }
            }

            string[] args;
            try { args = Environment.GetCommandLineArgs(); }
            catch { return false; }

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == null) continue;
                // Steam lobby path is not a host:port join.
                if (string.Equals(a, "+connect_lobby", StringComparison.OrdinalIgnoreCase))
                {
                    i++; // skip lobby id token if present
                    continue;
                }

                string val = null;
                if (a.StartsWith("-connect=", StringComparison.OrdinalIgnoreCase)
                    || a.StartsWith("+connect=", StringComparison.OrdinalIgnoreCase))
                {
                    val = a.Substring(a.IndexOf('=') + 1);
                }
                else if (string.Equals(a, "-connect", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(a, "+connect", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) val = args[++i];
                }

                if (val == null) continue;
                if (TryParse(val, out host, out port, out _))
                {
                    source = a.Contains("=") ? a : (a + " " + val);
                    return true;
                }
            }

            return false;
        }

        /// <summary>Same path as stock "Connect by IP" UI (GameServerInfo IP + Port → ConnectionManager.Connect).</summary>
        public static bool TryConnect(string host, int port, out string message)
        {
            message = null;
            try
            {
                var cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
                if (cm == null)
                {
                    message = "ConnectionManager not ready";
                    return false;
                }

                if (cm.IsConnected)
                {
                    message = "already connected; disconnect first";
                    return false;
                }

                // Prefer IPv4 when DNS returns mixed (matches stock direct-connect UI).
                string ip = host;
                if (!IPAddress.TryParse(host, out _))
                {
                    try
                    {
                        var entry = Dns.GetHostEntry(host);
                        if (entry.AddressList == null || entry.AddressList.Length == 0)
                        {
                            message = "no IP for hostname " + host;
                            return false;
                        }
                        ip = entry.AddressList[0].ToString();
                        for (int i = 0; i < entry.AddressList.Length; i++)
                        {
                            if (entry.AddressList[i].AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                ip = entry.AddressList[i].ToString();
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        message = "DNS failed for " + host + ": " + ex.Message;
                        return false;
                    }
                }

                var gsi = new GameServerInfo();
                gsi.SetValue(GameInfoString.IP, ip);
                gsi.SetValue(GameInfoInt.Port, port);
                // worldInfoCo writes RemoteWorldInfo from LastGameServerInfo.ServerVersion
                // (VersionInformation.TryParseSerializedString) and uses LevelName/WorldSize
                // for local-world matching. Direct-connect UI only sets IP+Port, which leaves
                // ServerVersion empty and logs "Failed writing RemoteWorldInfo".
                gsi.SetValue(GameInfoString.GameType, "7DTD");
                gsi.SetValue(GameInfoString.GameName, "zdtd");
                gsi.SetValue(GameInfoString.GameHost, "zdtd");
                gsi.SetValue(GameInfoString.LevelName, "Navezgane");
                gsi.SetValue(GameInfoString.GameMode, "Survival");
                string ver = "V.3.1.4";
                try
                {
                    if (Constants.cVersionInformation != null
                        && !string.IsNullOrEmpty(Constants.cVersionInformation.SerializableString))
                        ver = Constants.cVersionInformation.SerializableString;
                }
                catch { /* keep fallback */ }
                gsi.SetValue(GameInfoString.ServerVersion, ver);
                gsi.SetValue(GameInfoInt.WorldSize, 6144);
                gsi.SetValue(GameInfoInt.CurrentPlayers, 0);
                gsi.SetValue(GameInfoInt.MaxPlayers, 8);
                gsi.SetValue(GameInfoInt.FreePlayerSlots, 8);
                gsi.SetValue(GameInfoBool.IsDedicated, true);
                gsi.SetValue(GameInfoBool.EACEnabled, false);
                gsi.SetValue(GameInfoBool.IsPasswordProtected, false);

                if (GameManager.Instance != null)
                    GameManager.Instance.showOpenerMovieOnLoad = false;

                // DoSpawn opens XUiC_SpawnSelectionWindow unless SkipSpawnButton is true.
                // Auto-connect needs the direct RequestToSpawn path (no UI click).
                try
                {
                    GamePrefs.Set(EnumGamePrefs.SkipSpawnButton, true);
                }
                catch (Exception ex)
                {
                    Log.Warning("[zdtd-connect] SkipSpawnButton set failed: " + ex.Message);
                }

                Log.Out($"[zdtd-connect] Connect by IP {ip}:{port} ver={ver} level=Navezgane SkipSpawn=true (requested host={host})");
                cm.LastGameServerInfo = gsi;
                cm.Connect(gsi);
                message = $"connecting to {ip}:{port}";
                return true;
            }
            catch (Exception ex)
            {
                // Full stack: ProtocolManager.SetupProtocols NRE is otherwise silent.
                message = ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace;
                return false;
            }
        }
    }

    /// <summary>
    /// Gates auto-join until stock platform networking can SetupProtocols without NRE.
    /// NativePlatform null → HasNetworkingEnabled NRE before LiteNet Connect log.
    /// </summary>
    public static class ConnectReady
    {
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

                // Crossplay/EOS login is optional for EAC-off LAN stock dedi (our BotMod target).
                // Do not hard-block on PlatformUserId==null; just log. The server accepts LiteNet without EOS.
                try
                {
                    var cross = Platform.PlatformManager.CrossplatformPlatform;
                    if (cross != null)
                    {
                        var user = cross.User;
                        if (user != null && user.PlatformUserId == null)
                        {
                            Log.Out("[zdtd-connect] note: Crossplatform.User.PlatformUserId=null but proceeding (EAC off LAN)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Out("[zdtd-connect] cross-user note: " + ex.Message);
                }

                // Native steam user also optional when EAC off; proceed after 12s even if null.
                try
                {
                    var nUser = native.User;
                    if (nUser != null && nUser.PlatformUserId == null)
                    {
                        // Only block for first ~15s; after that, connect anyway (stock accepts unauthed LiteNet when EAC off).
                        if (GameManager.Instance != null && UnityEngine.Time.unscaledTime < 16f)
                        {
                            reason = "Native.User.PlatformUserId=null (early; retry in a moment)";
                            return false;
                        }
                        Log.Out("[zdtd-connect] note: Native.User.PlatformUserId=null past boot window, proceeding anyway");
                    }
                }
                catch (Exception ex)
                {
                    Log.Out("[zdtd-connect] native-user note: " + ex.Message);
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
