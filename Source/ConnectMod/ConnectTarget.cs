using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace SdtdConnect
{
    /// <summary>Parse host:port from env / argv / console and drive stock ConnectionManager.Connect.</summary>
    public static class ConnectTarget
    {
        public const string EnvVar = "7DTD_CONNECT";
        public const int DefaultPort = 27025;

        // Wire-protocol port range; not a tunable.
        const int MinPort = 1;
        const int MaxPort = 65535;

        // Placeholders for the GameServerInfo fields the stock direct-connect
        // UI leaves unset. worldInfoCo writes RemoteWorldInfo from
        // LastGameServerInfo and uses LevelName/WorldSize to match a local
        // world; with them empty it logs "Failed writing RemoteWorldInfo".
        // The server replaces every one of these at handshake, so they only
        // have to parse, not to be true. ServerVersion is the exception: the
        // running client's own version is used when it can be read, because a
        // stale literal would advertise a mismatch against itself. The literal
        // below is therefore the fallback for an unreadable
        // Constants.cVersionInformation, and it has to be updated when the game
        // version moves: the same value is recorded in pyproject.toml
        // ([tool.fastconnect] game-version) and test_version_sync.sh fails if
        // the two drift.
        const string PlaceholderGameType = "7DTD";
        const string PlaceholderGameName = "zdtd";
        const string PlaceholderLevelName = "Navezgane";
        const string PlaceholderGameMode = "Survival";
        const string PlaceholderServerVersion = "V.3.1.4";
        const int PlaceholderWorldSize = 6144;
        const int PlaceholderMaxPlayers = 8;

        // Both the boot-mode probe and the menu-open auto-join read the same
        // launch context; warn once so an invalid value cannot sit in the log
        // twice or, worse, look like "no target set".
        static bool _badTargetWarned;

        // Every launch-context value this module echoes goes through LogText,
        // the one implementation of the character rule. Env and argv values
        // are attacker-shapable (a clicked steam://run URL chooses -connect=
        // text), and join harnesses grep the client log for fixed markers, so
        // the reported source has to be the line a reader and a grep both
        // agree on. EchoForMessage rather than the bare sanitizer: the same
        // value can carry any length, and the harness only ever needs enough
        // of it to recognize what was tried.
        //
        // The rejection warning ends with "auto-join disabled", which is what a
        // rejected value means on its own. A rejected env var does not stop the
        // argv scan, so a later -connect= can still resolve and the same log
        // then reads as "disabled" right next to a live join. One follow-up
        // line names the source that won; latched, so the re-reads the boot
        // probe and the menu open make do not repeat it.
        static bool _acceptedAfterRejectionLogged;

        static void NoteAcceptedAfterRejection(string source)
        {
            if (!_badTargetWarned || _acceptedAfterRejectionLogged) return;
            _acceptedAfterRejectionLogged = true;
            Log.Out("[7dtd-fastconnect] auto-join target accepted from " + source
                + "; the rejected launch-context value above was ignored, not fatal");
        }

        static void WarnIgnoredTarget(string sourceLabel, string raw, string error)
        {
            if (_badTargetWarned) return;
            _badTargetWarned = true;
            // EchoForMessage, not SanitizeForLog: this is the one launch-context
            // echo with no length cap, and a clicked steam://run URL chooses the
            // text, so an arbitrary-length value would land whole in the client
            // log the join harnesses scan.
            Log.Warning("[7dtd-fastconnect] " + LogText.SanitizeForLog(sourceLabel) + "='"
                + LogText.EchoForMessage(raw) + "' ignored: "
                + error + "; auto-join disabled (fix the value or use F1: connect <host> [port])");
        }

        // Shared normalization for every target grammar (F1 merge, env, argv),
        // so the entry paths cannot drift: a pasted steam://connect/ prefix is
        // stripped (its colons must not mask an explicit port), and dangling
        // separator colons ("host:", "[v6]:", the doubled "host::") carry an
        // empty port by TryParse's rule, so they are dropped instead of
        // leaving an unparsable host behind.
        static string StripSchemeAndEmptyPort(string raw)
        {
            if (raw.StartsWith("steam://connect/", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring("steam://connect/".Length);
            while (raw.EndsWith(":")) raw = raw.Substring(0, raw.Length - 1);
            return raw;
        }

        // Port-suffix rule shared by both grammar branches. Decimal digits
        // only, read in the invariant culture, so the value is the same text
        // for every client locale: the shell that launches the client
        // (is_tcp_port, under LC_ALL=C) rejects "+80", " 80" and non-ASCII
        // digits, and a port only a culture-sensitive parse would accept is a
        // value the harness that set it could not have set.
        static bool TryParsePort(string text, out int port)
        {
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                && port >= MinPort && port <= MaxPort;
        }

        // A missing host names the expected shape, so the message doubles as
        // the correction.
        const string MissingHostError = "missing host; expected host[:port], e.g. connect 127.0.0.1:27025";

        /// <summary>
        /// Merges an optional explicit port argument into a raw host string
        /// (normalized by StripSchemeAndEmptyPort first): the second token is
        /// only appended to a host that does not already carry a port. A bare
        /// IPv6 address gets the port appended in bracketed form: TryParse
        /// reads any ":port" suffix off a bare IPv6 as part of the address,
        /// so the merged string must come back as [addr]:port to round-trip.
        /// portArg=null keeps just the strips.
        /// </summary>
        public static string MergePortArg(string raw, string portArg)
        {
            if (raw == null) return null;
            raw = StripSchemeAndEmptyPort(raw);
            bool hasPort;
            bool bracketed = raw.StartsWith("[");
            if (bracketed)
            {
                int close = raw.IndexOf(']');
                hasPort = close >= 0 && close < raw.Length - 1 && raw[close + 1] == ':';
            }
            else
            {
                int firstColon = raw.IndexOf(':');
                hasPort = firstColon >= 0 && firstColon == raw.LastIndexOf(':');
            }
            if (hasPort)
            {
                // A host that already carries a port wins, so `connect
                // host:1234 5678` joins 1234 while the operator asked for
                // 5678. Name the token that was dropped instead of letting the
                // join land somewhere they did not ask for.
                if (portArg != null) WarnDroppedPortArg(raw, portArg);
                return raw;
            }
            if (portArg == null) return raw;
            // Hostnames and IPv4 never contain ':', so a colon here means bare
            // IPv6; only the bracketed form survives TryParse with the port.
            return bracketed || raw.IndexOf(':') < 0
                ? raw + ":" + portArg
                : "[" + raw + "]:" + portArg;
        }

        // The console command merges once per invocation; the fuzz lane merges
        // thousands of times, so the note is latched like the other one-shot
        // launch-context warnings.
        static bool _droppedPortArgWarned;

        static void WarnDroppedPortArg(string host, string portArg)
        {
            if (_droppedPortArgWarned) return;
            _droppedPortArgWarned = true;
            Log.Warning("[7dtd-fastconnect] port argument '" + LogText.EchoForMessage(portArg)
                + "' ignored: '" + LogText.EchoForMessage(host) + "' already carries a port");
        }

        public static bool TryParse(string raw, out string host, out int port, out string error)
        {
            host = null;
            port = DefaultPort;
            error = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = MissingHostError;
                return false;
            }

            // Accept host, host:port, or a pasted steam://connect/ URL.
            raw = StripSchemeAndEmptyPort(raw.Trim());

            string hostPart = raw;
            int portPart = DefaultPort;

            // The port suffix is split off by whichever grammar matches, then
            // range-checked once; both grammars reject a bad port the same way.
            string portText = null;

            // IPv6 in brackets: [addr]:port
            if (raw.StartsWith("["))
            {
                int close = raw.IndexOf(']');
                if (close < 0)
                {
                    error = "unclosed '[' in the IPv6 host; write it as [addr] or [addr]:port";
                    return false;
                }
                hostPart = raw.Substring(1, close - 1);
                if (close + 1 < raw.Length && raw[close + 1] == ':')
                    portText = raw.Substring(close + 2);
            }
            else
            {
                // Last colon separates port (IPv4 / hostname).
                int colon = raw.LastIndexOf(':');
                if (colon > 0 && colon < raw.Length - 1
                    && raw.IndexOf(':') == colon) // single colon → not bare IPv6
                {
                    portText = raw.Substring(colon + 1);
                    hostPart = raw.Substring(0, colon);
                }
            }

            // The rejected port is echoed with the accepted range: "bad port"
            // alone leaves the operator guessing which of host and port is wrong.
            if (portText != null && !TryParsePort(portText, out portPart))
            {
                error = "port must be a number from " + MinPort + " to " + MaxPort
                    + " (got '" + LogText.EchoForMessage(portText) + "')";
                return false;
            }

            if (string.IsNullOrWhiteSpace(hostPart))
            {
                error = MissingHostError;
                return false;
            }

            // A lone leading colon (":27025", ":abc") is an empty host before
            // a port: no hostname or IPv4 literal starts with ':', and bare
            // IPv6 always carries '::'. Accepted here it would defer failure
            // to a DNS lookup while the caller's port silently falls back to
            // the default.
            if (hostPart.StartsWith(":") && !hostPart.StartsWith("::"))
            {
                error = MissingHostError;
                return false;
            }

            host = hostPart.Trim();
            port = portPart;
            return true;
        }

        /// <summary>
        /// Env (7DTD_CONNECT) first, then argv: -connect= / +connect= and the
        /// space-separated -connect / +connect, with a +connect_lobby argument
        /// and its token skipped. An unparseable value is warned about and the
        /// scan continues, so one bad flag does not hide a good later one.
        /// </summary>
        public static bool TryFromLaunchContext(out string host, out int port, out string source)
        {
            host = null;
            port = DefaultPort;
            source = null;

            string env = EnvFlags.Read(EnvVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                if (TryParse(env, out host, out port, out string envError))
                {
                    source = EnvVar + "=" + LogText.EchoForMessage(env.Trim());
                    NoteAcceptedAfterRejection(source);
                    return true;
                }
                WarnIgnoredTarget(EnvVar, env.Trim(), envError);
            }

            // Same rule as the env read above, for the same reason: a blocked
            // argv read is indistinguishable from a launch with no -connect,
            // and letting the exception escape would abort mod load from a
            // static initializer.
            string[] args;
            try
            {
                args = Environment.GetCommandLineArgs();
            }
            catch (Exception)
            {
                return false;
            }
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
                int eq = a.IndexOf('=');
                if (TryParse(val, out host, out port, out string argError))
                {
                    source = a.Contains("=")
                        ? LogText.EchoForMessage(a)
                        : LogText.EchoForMessage(a) + " " + LogText.EchoForMessage(val);
                    NoteAcceptedAfterRejection(source);
                    return true;
                }
                // Only the flag name; the value is already in the message.
                string label = eq >= 0 ? a.Substring(0, eq) : a;
                WarnIgnoredTarget(label, val, argError);
            }

            return false;
        }

        // Resolves a hostname to an address, preferring IPv4 when DNS returns
        // mixed families (matches stock direct-connect UI). Literal IPs pass
        // through untouched.
        static bool ResolveHostIPv4(string host, out string ip, out string message)
        {
            ip = host;
            message = null;
            if (IPAddress.TryParse(host, out _)) return true;
            try
            {
                // GetHostEntry has no timeout; a wedged resolver would
                // freeze the menu thread for the OS retry window. Bound
                // the wait and report instead.
                const int dnsTimeoutMs = 5000;
                var pending = Dns.BeginGetHostEntry(host, null, null);
                try
                {
                    if (!pending.AsyncWaitHandle.WaitOne(dnsTimeoutMs))
                    {
                        message = "DNS timed out after " + (dnsTimeoutMs / 1000) + "s for " + LogText.EchoForMessage(host);
                        return false;
                    }
                    var entry = Dns.EndGetHostEntry(pending);
                    if (entry.AddressList == null || entry.AddressList.Length == 0)
                    {
                        message = "no IP for hostname " + LogText.EchoForMessage(host);
                        return false;
                    }
                    // First address is the default; only a later one can
                    // replace it, and only with IPv4. Starting at 1 keeps the
                    // scan from re-testing the entry already used.
                    ip = entry.AddressList[0].ToString();
                    for (int i = 1; i < entry.AddressList.Length; i++)
                    {
                        if (entry.AddressList[i].AddressFamily == AddressFamily.InterNetwork)
                        {
                            ip = entry.AddressList[i].ToString();
                            break;
                        }
                    }
                }
                finally
                {
                    // Close on an already-disposed wait handle throws
                    // ObjectDisposedException; the handle is unreachable
                    // either way and this runs on the success path of a
                    // resolve the caller is about to use.
                    try { pending.AsyncWaitHandle.Close(); }
                    catch (ObjectDisposedException) { }
                }
                return true;
            }
            catch (Exception ex)
            {
                message = "DNS failed for " + LogText.EchoForMessage(host) + ": "
                    + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // One connect attempt at a time per process. ConnectionManager.Connect
        // hands the target to LiteNetLib and returns; IsConnected reports the
        // outcome only after the handshake, so every other check in TryConnect
        // still passes while an attempt is running and a second request starts a
        // second attempt against a server the first one is still dialling. Two
        // callers reach TryConnect in one menu: the auto-join coroutine, and
        // the F1 command, which a double keypress or a pasted repeat repeats.
        enum ConnectRequest { Idle, InFlight, Connected }

        static ConnectRequest _request = ConnectRequest.Idle;
        static float _requestStartedAt;
        static string _requestTarget;

        // How long a request the client never reports a connection holds the
        // latch. A local or dev-server attempt completes, or gives up, well
        // inside this; the window only releases a request whose outcome the mod
        // never saw, so a failed join can be retried instead of wedged. It is
        // not a budget for a slow join: a request that lands is released by
        // NoteConnected, however long the handshake took.
        const float ConnectRequestWindowSec = 30f;

        /// <summary>
        /// Records that a request this mod made reached the server.
        /// </summary>
        internal static void NoteConnected()
        {
            if (_request == ConnectRequest.InFlight) _request = ConnectRequest.Connected;
        }

        /// <summary>
        /// Turns a request from in-flight into connected when the client is in
        /// a live session. The join gate stops polling the moment it fires a
        /// request, so it cannot be the only observer: the frame hook that
        /// runs for the whole session is, and without it the refusal only
        /// lifts when the 30 s window expires, long after the join landed.
        /// </summary>
        internal static void NoteConnectedIfSessionLive()
        {
            try
            {
                var cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
                if (cm != null && cm.IsConnected) NoteConnected();
            }
            catch (Exception)
            {
                // A singleton torn down mid-frame is the usual cause, and the
                // latch carries its own window, so a miss here costs at most
                // that window.
            }
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
                    message = "client not ready to connect yet; retry in a moment from the main menu";
                    return false;
                }

                if (cm.IsConnected)
                {
                    // A request this mod made reached the server, whoever
                    // started it: the auto-join poll (ConnectReady) normally
                    // reports that, but the F1 command has no poll behind it,
                    // and without this its attempt holds the latch until the
                    // window expires, so the operator's next join is refused
                    // as a duplicate of a session they had already left.
                    NoteConnected();
                    message = "already connected; disconnect first";
                    return false;
                }

                // A request that was seen to reach the server is not in flight
                // any more, and the client is off that session now (the check
                // above), so this is a new join rather than a repeat of it.
                if (_request == ConnectRequest.Connected)
                    _request = ConnectRequest.Idle;

                if (_request == ConnectRequest.InFlight)
                {
                    if (UnityEngine.Time.realtimeSinceStartup - _requestStartedAt < ConnectRequestWindowSec)
                    {
                        message = "a connect to " + _requestTarget
                            + " is already in flight; wait for it to finish";
                        return false;
                    }
                    // Nothing reported back for longer than the window: the
                    // attempt is over as far as this client can tell, so a
                    // retry must not be refused as a duplicate.
                    _request = ConnectRequest.Idle;
                }

                if (!ResolveHostIPv4(host, out string ip, out message))
                    return false;

                var gsi = new GameServerInfo();
                gsi.SetValue(GameInfoString.IP, ip);
                gsi.SetValue(GameInfoInt.Port, port);
                gsi.SetValue(GameInfoString.GameType, PlaceholderGameType);
                gsi.SetValue(GameInfoString.GameName, PlaceholderGameName);
                gsi.SetValue(GameInfoString.GameHost, PlaceholderGameName);
                gsi.SetValue(GameInfoString.LevelName, PlaceholderLevelName);
                gsi.SetValue(GameInfoString.GameMode, PlaceholderGameMode);
                string ver = PlaceholderServerVersion;
                try
                {
                    if (Constants.cVersionInformation != null
                        && !string.IsNullOrEmpty(Constants.cVersionInformation.SerializableString))
                        ver = Constants.cVersionInformation.SerializableString;
                }
                catch (Exception)
                {
                    // Reading the client's own version string is a nicety;
                    // PlaceholderServerVersion still parses, and the server
                    // overwrites the field at handshake either way.
                }
                gsi.SetValue(GameInfoString.ServerVersion, ver);
                gsi.SetValue(GameInfoInt.WorldSize, PlaceholderWorldSize);
                gsi.SetValue(GameInfoInt.CurrentPlayers, 0);
                gsi.SetValue(GameInfoInt.MaxPlayers, PlaceholderMaxPlayers);
                gsi.SetValue(GameInfoInt.FreePlayerSlots, PlaceholderMaxPlayers);
                gsi.SetValue(GameInfoBool.IsDedicated, true);
                gsi.SetValue(GameInfoBool.EACEnabled, false);
                gsi.SetValue(GameInfoBool.IsPasswordProtected, false);

                if (GameManager.Instance != null)
                    GameManager.Instance.showOpenerMovieOnLoad = false;

                Log.Out($"[7dtd-fastconnect] Connect by IP {ip}:{port} ver={ver} level={PlaceholderLevelName} (requested host={LogText.EchoForMessage(host)})");
                cm.LastGameServerInfo = gsi;
                cm.Connect(gsi);
                // Armed only once the attempt is under way: a Connect that
                // threw never dialled anything, so it must not refuse the retry
                // that follows it.
                _request = ConnectRequest.InFlight;
                _requestStartedAt = UnityEngine.Time.realtimeSinceStartup;
                _requestTarget = ip + ":" + port;
                message = $"connecting to {ip}:{port}";
                return true;
            }
            catch (Exception ex)
            {
                // Full stack: ProtocolManager.SetupProtocols NRE is otherwise silent.
                // Both parts are flattened, not just the message: a stack frame
                // carries assembly and method names the exception built its
                // own text from, and this line goes to the on-screen F1 console
                // as well as the log. The deliberate newline before the stack
                // trace stays, so the trace still reads as its own block.
                message = ex.GetType().Name + ": " + LogText.SanitizeForLog(ex.Message)
                    + "\n" + LogText.SanitizeForLog(ex.StackTrace);
                return false;
            }
        }
    }
}
