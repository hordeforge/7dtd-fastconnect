using System.Collections.Generic;

namespace SdtdConnect
{
    /// <summary>F1 console: connect &lt;host&gt; [port] (main menu).</summary>
    public class ConsoleCmdConnect : ConsoleCmdAbstract
    {
        public override string[] getCommands() => new[] { "connect", "7dtdconnect", "joinip" };

        public override string getDescription() =>
            "Connect to a server by IP (same as Connect to IP UI). Default port 27025.";

        public override string getHelp() =>
            "connect <host> [port]\n" +
            "  Examples:\n" +
            "    connect 127.0.0.1\n" +
            "    connect 127.0.0.1 27025\n" +
            "    connect 127.0.0.1:27025\n" +
            "  Env auto-join: 7DTD_CONNECT=127.0.0.1:27025\n" +
            "  Launch arg: -connect=127.0.0.1:27025\n" +
            "  Note: C# client mods require EAC off (-noeac).";

        public override bool AllowedInMainMenu => true;

        public override bool IsExecuteOnClient => true;

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            if (_params == null || _params.Count < 1)
            {
                Out(getHelp());
                return;
            }

            string raw = _params[0];
            // Strip the steam-style scheme first (same normalization as
            // ConnectTarget.TryParse) so its colons cannot mask an explicit
            // port arg. Then mirror TryParse's port rule: bracketed IPv6
            // hosts only take "]:" as an explicit-port separator, plain
            // hosts only carry a port after a SINGLE colon (bare IPv6 has
            // several, so an explicit port arg must still be appended).
            const string steamPrefix = "steam://connect/";
            if (raw.StartsWith(steamPrefix, System.StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(steamPrefix.Length);
            int firstColon = raw.IndexOf(':');
            bool hasPort = raw.StartsWith("[")
                ? raw.Contains("]:")
                : firstColon >= 0 && firstColon == raw.LastIndexOf(':');
            if (_params.Count >= 2 && !hasPort)
                raw = raw + ":" + _params[1];

            if (!ConnectTarget.TryParse(raw, out string host, out int port, out string err))
            {
                Out("[7dtd-connect] parse failed: " + err);
                return;
            }

            if (!ConnectTarget.TryConnect(host, port, out string msg))
            {
                Out("[7dtd-connect] " + msg);
                return;
            }

            Out("[7dtd-connect] " + msg);
        }

        static void Out(string s)
        {
            try { SingletonMonoBehaviour<SdtdConsole>.Instance?.Output(s); }
            catch { /* ignore */ }
            Log.Out(s);
        }
    }
}
