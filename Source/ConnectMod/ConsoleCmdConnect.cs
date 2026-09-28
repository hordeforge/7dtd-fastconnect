using System;
using System.Collections.Generic;

namespace SdtdConnect
{
    /// <summary>F1 console: connect &lt;host&gt; [port] (main menu).</summary>
    public class ConsoleCmdConnect : ConsoleCmdAbstract
    {
        public override string[] getCommands() => new[] { "connect", "7dtdconnect", "joinip" };

        public override string getDescription() =>
            "Connect to a server by IP (same as Connect to IP UI). Default port 27025. Aliases: 7dtdconnect, joinip.";

        public override string getHelp() =>
            "connect <host> [port]\n" +
            "  Examples:\n" +
            "    connect 127.0.0.1\n" +
            "    connect 127.0.0.1 27025\n" +
            "    connect 127.0.0.1:27025\n" +
            "  The reply names the target it dialled; a second line follows once\n" +
            "  the attempt ends (connected, or the window closing with no session).\n" +
            "  Env auto-join: 7DTD_CONNECT=127.0.0.1:27025\n" +
            "  Launch arg: -connect=127.0.0.1:27025\n" +
            "  Note: C# client mods require EAC off (-noeac).";

        public override bool AllowedInMainMenu => true;

        public override bool IsExecuteOnClient => true;

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            if (_params == null || _params.Count < 1)
            {
                ConsoleOutput.Out(getHelp());
                return;
            }

            // Scheme strip + optional explicit port arg follow ConnectTarget's
            // own rules via MergePortArg, so this command cannot drift from
            // TryParse when the grammar changes.
            string raw = ConnectTarget.MergePortArg(
                _params[0], _params.Count >= 2 ? _params[1] : null);

            // A third token has no place in the grammar (host [port]). Saying so
            // is the same rule MergePortArg applies to a port the host already
            // carries: a silently swallowed token lands the join somewhere the
            // operator did not ask for.
            if (_params.Count > 2)
                ConsoleOutput.Fail("[7dtd-fastconnect] connect: ignoring extra argument(s) '"
                    + LogText.EchoForMessage(string.Join(" ", _params.GetRange(2, _params.Count - 2)))
                    + "'; the command takes host [port]");

            if (!ConnectTarget.TryParse(raw, out string host, out int port, out string err))
            {
                // Echo what was typed: the reason names the part that is
                // wrong, the echo says which part the console read. The echo
                // is labeled rather than parenthesized because a reason that
                // already quotes the offending half ("got '27025x'") would
                // otherwise read as two echoes of one message.
                ConsoleOutput.Fail("[7dtd-fastconnect] connect failed: " + err
                    + " [typed '" + LogText.EchoForMessage(raw) + "']");
                return;
            }

            // Success and failure both report `msg`, at the severity the
            // auto-join path uses for the same outcome (ModApi.ConnectAndLog),
            // so one log records a connect the same way from either entry
            // point.
            if (ConnectTarget.TryConnect(host, port, out string msg))
            {
                // The outcome line: the console is the only place a person
                // waits for an answer, and TryConnect's own message only says
                // the attempt started.
                ConnectTarget.WatchConsoleRequest(host + ":" + port);
                ConsoleOutput.Out("[7dtd-fastconnect] " + msg);
            }
            else
                ConsoleOutput.Fail("[7dtd-fastconnect] connect failed: " + msg);
        }
    }
}
