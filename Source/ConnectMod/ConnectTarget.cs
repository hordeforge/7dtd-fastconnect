using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

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
        // version moves: nothing else in the tree records that version.
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

        // Unicode format characters a terminal renders as nothing (or as a
        // line reorder) but a reader or a grep of the text still sees: the
        // bidi overrides and embeddings, the LTR/RTR marks, the zero-width
        // space/joiner/non-joiner, and the BOM. char.IsControl does not cover
        // them (they are Cf, not Cc), so each is checked by range here.
        static bool IsInvisibleFormat(char c)
        {
            return (c >= '\u200B' && c <= '\u200F')   // ZWSP, ZWNJ, ZWJ, LRM, RLM
                || (c >= '\u2060' && c <= '\u2064')   // word joiner, invisible operators
                || (c >= '\u2066' && c <= '\u2069')   // LRI, RLI, FSI, PDI
                || (c >= '\u202A' && c <= '\u202E')   // LRE, RLE, PDF, LRO, RLO
                || c == '\uFEFF';                    // BOM / zero-width no-break space
        }

        /// <summary>
        /// Flattens control, line-breaking and invisible-format characters so
        /// a launch-context string stays one readable log line. Env and argv
        /// values are attacker-shapable (a clicked steam://run URL chooses
        /// -connect= text), and join harnesses grep the client log for fixed
        /// markers; an embedded newline could forge those markers without
        /// ever connecting, a U+2028 breaks the line in a reader that grep
        /// reads as one, and a bidi override could render a forged line
        /// that reads differently from the text a grep sees. One character
        /// becomes one space, so offsets and lengths are preserved.
        /// The stricter twin of LogText.SanitizeForLog, which this module
        /// uses for every launch-context value it echoes: the reported source
        /// is the line a reader and a grep both have to agree on.
        /// </summary>
        internal static string SanitizeForLog(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            bool dirty = false;
            foreach (char c in value)
            {
                if (IsLineBreaking(c) || IsInvisibleFormat(c)) { dirty = true; break; }
            }
            if (!dirty) return value;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(IsLineBreaking(c) || IsInvisibleFormat(c) ? ' ' : c);
            return sb.ToString();
        }

        // char.IsControl covers C0, DEL and C1 but not the Unicode line and
        // paragraph separators, which a log reader lays out as a line break
        // even though grep does not: the same forged-marker shape, one layer
        // down. The shell twin (scripts/log_sanitize.sh) flattens the same set.
        static bool IsLineBreaking(char c)
        {
            return char.IsControl(c) || c == '\u2028' || c == '\u2029';
        }

        /// <summary>
        /// One-line echo of operator input for an error message: control
        /// characters flattened, long pastes cut so a mistyped paste cannot
        /// scroll the reason off screen. The cut counts code points and never
        /// splits a surrogate pair (TextUtil), so the echo cannot put a lone
        /// surrogate in front of the operator.
        /// </summary>
        internal static string EchoForMessage(string value)
        {
            const int maxChars = 40;
            if (string.IsNullOrEmpty(value)) return value;
            string flat = SanitizeForLog(value).Trim();
            if (TextUtil.CodePointCount(flat) <= maxChars) return flat;
            return TextUtil.TruncateToCodePoints(flat, maxChars) + "...";
        }

        static void WarnIgnoredTarget(string sourceLabel, string raw, string error)
        {
            if (_badTargetWarned) return;
            _badTargetWarned = true;
            Log.Warning("[7dtd-fastconnect] " + SanitizeForLog(sourceLabel) + "='"
                + SanitizeForLog(raw) + "' ignored: "
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

        // Port-suffix rule shared by both grammar branches.
        static bool TryParsePort(string text, out int port)
        {
            return int.TryParse(text, out port) && port >= MinPort && port <= MaxPort;
        }

        // The rejected port is echoed with the accepted range: "bad port"
        // alone leaves the operator guessing which of host and port is wrong.
        static string BadPortError(string text)
        {
            return "port must be a number from " + MinPort + " to " + MaxPort
                + " (got '" + LogText.EchoForMessage(text) + "')";
        }

        // Same for a missing host: the expected shape is spelled out so the
        // message doubles as the correction.
        static string MissingHostError()
        {
            return "missing host; expected host[:port], e.g. connect 127.0.0.1:27025";
        }

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
            Log.Warning("[7dtd-fastconnect] port argument '" + SanitizeForLog(portArg)
                + "' ignored: '" + SanitizeForLog(host) + "' already carries a port");
        }

        public static bool TryParse(string raw, out string host, out int port, out string error)
        {
            host = null;
            port = DefaultPort;
            error = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = MissingHostError();
                return false;
            }

            // Accept host, host:port, or a pasted steam://connect/ URL.
            raw = StripSchemeAndEmptyPort(raw.Trim());

            string hostPart = raw;
            int portPart = DefaultPort;

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
                {
                    string portText = raw.Substring(close + 2);
                    if (!TryParsePort(portText, out portPart))
                    {
                        error = BadPortError(portText);
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
                    string portText = raw.Substring(colon + 1);
                    if (!TryParsePort(portText, out portPart))
                    {
                        error = BadPortError(portText);
                        return false;
                    }
                    hostPart = raw.Substring(0, colon);
                }
                else
                    hostPart = raw;
            }

            if (string.IsNullOrWhiteSpace(hostPart))
            {
                error = MissingHostError();
                return false;
            }

            // A lone leading colon (":27025", ":abc") is an empty host before
            // a port: no hostname or IPv4 literal starts with ':', and bare
            // IPv6 always carries '::'. Accepted here it would defer failure
            // to a DNS lookup while the caller's port silently falls back to
            // the default.
            if (hostPart.StartsWith(":") && !hostPart.StartsWith("::"))
            {
                error = MissingHostError();
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

            string env = EnvFlags.Read(EnvVar);
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
                        message = "DNS timed out after " + (dnsTimeoutMs / 1000) + "s for " + SanitizeForLog(host);
                        return false;
                    }
                    var entry = Dns.EndGetHostEntry(pending);
                    if (entry.AddressList == null || entry.AddressList.Length == 0)
                    {
                        message = "no IP for hostname " + SanitizeForLog(host);
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
                message = "DNS failed for " + SanitizeForLog(host) + ": "
                    + ex.GetType().Name + ": " + ex.Message;
                return false;
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
                    message = "already connected; disconnect first";
                    return false;
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

                Log.Out($"[7dtd-fastconnect] Connect by IP {ip}:{port} ver={ver} level={PlaceholderLevelName} (requested host={SanitizeForLog(host)})");
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
}
