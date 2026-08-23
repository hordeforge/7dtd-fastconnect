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

            // Scheme strip + optional explicit port arg follow ConnectTarget's
            // own rules via MergePortArg, so this command cannot drift from
            // TryParse when the grammar changes.
            string raw = ConnectTarget.MergePortArg(
                _params[0], _params.Count >= 2 ? _params[1] : null);

            if (!ConnectTarget.TryParse(raw, out string host, out int port, out string err))
            {
                Out("[7dtd-fastconnect] parse failed: " + err);
                return;
            }

            // Success and failure both report `msg` to the console; the
            // return value adds nothing here.
            ConnectTarget.TryConnect(host, port, out string msg);
            Out("[7dtd-fastconnect] " + msg);
        }

        static void Out(string s)
        {
            try { SingletonMonoBehaviour<SdtdConsole>.Instance?.Output(s); }
            catch { /* ignore */ }
            Log.Out(s);
        }
    }
}
