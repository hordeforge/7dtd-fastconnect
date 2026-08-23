using System;
using System.Net;
using System.Text;

namespace SdtdConnect
{
    /// <summary>Parse host:port from env / argv / console and drive stock ConnectionManager.Connect.</summary>
    public static class ConnectTarget
    {
        public const string EnvVar = "7DTD_CONNECT";
        public const int DefaultPort = 27025;

        // Both the boot-mode probe and the menu-open auto-join read the same
        // launch context; warn once so an invalid value cannot sit in the log
        // three times or, worse, look like "no target set".
        static bool _badTargetWarned;

        /// <summary>
        /// Flattens control characters so a launch-context string stays one
        /// log line. Env and argv values are attacker-shapable (a clicked
        /// steam://run URL chooses -connect= text), and join harnesses grep
        /// the client log for fixed markers; an embedded newline could forge
        /// those markers without ever connecting.
        /// </summary>
        internal static string SanitizeForLog(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            bool dirty = false;
            foreach (char c in value)
            {
                if (char.IsControl(c)) { dirty = true; break; }
            }
            if (!dirty) return value;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(char.IsControl(c) ? ' ' : c);
            return sb.ToString();
        }

        static void WarnIgnoredTarget(string sourceLabel, string raw, string error)
        {
            if (_badTargetWarned) return;
            _badTargetWarned = true;
            Log.Warning("[7dtd-fastconnect] " + SanitizeForLog(sourceLabel) + "='"
                + SanitizeForLog(raw) + "' ignored: "
                + error + "; auto-join disabled (fix the value or use F1: connect <host> [port])");
        }

        /// <summary>
        /// Merges an optional explicit port argument into a raw host string:
        /// strips a pasted steam://connect/ prefix (its colons must not mask
        /// an explicit port) and applies TryParse's port rule so the second
        /// token is only appended to a host that does not already carry a
        /// port. A dangling separator colon ("host:", "[v6]:") is an empty
        /// port by that same rule, so it is dropped first: appending would
        /// otherwise double the colon, and passing through would leave an
        /// unparsable host behind. A bare IPv6 address gets the port appended
        /// in bracketed form: TryParse reads any ":port" suffix off a bare
        /// IPv6 as part of the address, so the merged string must come back
        /// as [addr]:port to round-trip. portArg=null keeps just the strips.
        /// </summary>
        public static string MergePortArg(string raw, string portArg)
        {
            if (raw == null) return null;
            if (raw.StartsWith("steam://connect/", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring("steam://connect/".Length);
            bool hasPort;
            bool bracketed = raw.StartsWith("[");
            if (bracketed)
            {
                if (raw.EndsWith(":")) raw = raw.Substring(0, raw.Length - 1);
                int close = raw.IndexOf(']');
                hasPort = close >= 0 && close < raw.Length - 1 && raw[close + 1] == ':';
            }
            else
            {
                if (raw.EndsWith(":")) raw = raw.Substring(0, raw.Length - 1);
                int firstColon = raw.IndexOf(':');
                hasPort = firstColon >= 0 && firstColon == raw.LastIndexOf(':');
            }
            if (hasPort || portArg == null) return raw;
            // Hostnames and IPv4 never contain ':', so a colon here means bare
            // IPv6; only the bracketed form survives TryParse with the port.
            return bracketed || raw.IndexOf(':') < 0
                ? raw + ":" + portArg
                : "[" + raw + "]:" + portArg;
        }

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
            // Accept host, host:port, or a pasted steam://connect/ URL (scheme stripped).
            if (raw.StartsWith("steam://connect/", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring("steam://connect/".Length);
            // Same dangling-separator rule MergePortArg applies: "host:" /
            // "[v6]:" carry an empty port, so drop the colon instead of
            // leaving an unparsable host behind (env/argv reach TryParse
            // directly and would otherwise diverge from the F1 command).
            if (raw.EndsWith(":")) raw = raw.Substring(0, raw.Length - 1);

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

        /// <summary>Env (7DTD_CONNECT), then -connect= / +connect from argv.</summary>
        public static bool TryFromLaunchContext(out string host, out int port, out string source)
        {
            host = null;
            port = DefaultPort;
            source = null;

            string env = Environment.GetEnvironmentVariable(EnvVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                if (TryParse(env, out host, out port, out string envError))
                {
                    source = EnvVar + "=" + SanitizeForLog(env.Trim());
                    return true;
                }
                WarnIgnoredTarget(EnvVar, env.Trim(), envError);
            }

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
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
                if (TryParse(val, out host, out port, out string argError))
                {
                    source = a.Contains("=")
                        ? SanitizeForLog(a)
                        : SanitizeForLog(a) + " " + SanitizeForLog(val);
                    return true;
                }
                // Only the flag name; the value is already in the message.
                string label = a.Contains("=") ? a.Substring(0, a.IndexOf('=')) : a;
                WarnIgnoredTarget(label, val, argError);
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
                        // GetHostEntry has no timeout; a wedged resolver would
                        // freeze the menu thread for the OS retry window. Bound
                        // the wait and report instead.
                        const int dnsTimeoutMs = 5000;
                        var pending = Dns.BeginGetHostEntry(host, null, null);
                        if (!pending.AsyncWaitHandle.WaitOne(dnsTimeoutMs))
                        {
                            message = "DNS timed out after " + (dnsTimeoutMs / 1000) + "s for " + SanitizeForLog(host);
                            return false;
                        }
                        var entry = Dns.EndGetHostEntry(pending);
                        if (entry.AddressList == null || entry.AddressList.Length == 0)
                        {
                            message = "no IP for hostname " + SanitizeForLog(host);
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
                        message = "DNS failed for " + SanitizeForLog(host) + ": " + ex.Message;
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
                // Interactive F1 joins keep stock behaviour: the pref persists,
                // so setting it outside automation would suppress the spawn
                // window in ordinary play too.
                if (AutomationMode.Enabled)
                {
                    try
                    {
                        GamePrefs.Set(EnumGamePrefs.SkipSpawnButton, true);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("[7dtd-fastconnect] SkipSpawnButton set failed: " + ex.Message);
                    }
                }

                Log.Out($"[7dtd-fastconnect] Connect by IP {ip}:{port} ver={ver} level=Navezgane SkipSpawn=true (requested host={SanitizeForLog(host)})");
                cm.LastGameServerInfo = gsi;
                cm.Connect(gsi);
                message = $"connecting to {ip}:{port}";
                return true;
            }
            catch (Exception ex)
            {
                // Full stack: ProtocolManager.SetupProtocols NRE is otherwise silent.
                // The message may echo the raw host, so only that part is flattened;
                // the deliberate newline before the stack trace stays.
                message = ex.GetType().Name + ": " + SanitizeForLog(ex.Message) + "\n" + ex.StackTrace;
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
        // Wall time (unscaled) when the cross user was first seen without an id.
        static float _crossWaitStart = -1f;

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
                            Log.Out("[7dtd-fastconnect] note: Crossplatform.User.PlatformUserId=null past wait window, proceeding anyway");
                        }
                        else if (user != null)
                        {
                            _crossWaitStart = -1f; // logged in; reset for later rejoins
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
                        Log.Out("[7dtd-fastconnect] note: Native.User.PlatformUserId=null past boot window, proceeding anyway");
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
