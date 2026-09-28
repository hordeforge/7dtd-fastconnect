using System.Collections.Generic;

namespace SdtdConnect
{
    /// <summary>F1 console: diag on/off/toggle/status, toggles verbose traces at runtime.</summary>
    public class ConsoleCmdDiag : ConsoleCmdAbstract
    {
        public override string[] getCommands() => new[] { "diag", "7dtd_diag", "zdiag" };
        public override string getDescription() => "Toggle verbose 7dtd-fastconnect diagnostics (opt-in, off by default).";

        public override string getHelp() =>
            "diag [on|off|toggle|status]\n" +
            "  diag on      enable verbose traces (also 1, enable, true)\n" +
            "  diag off     disable verbose traces (also 0, disable, false)\n" +
            "  diag toggle  flip (also flip)\n" +
            "  diag status  show current, plus this help when no argument is given\n" +
            "Launch with 7DTD_CONNECT_DEBUG=1 for verbose on boot. Otherwise off by default.";

        public override bool AllowedInMainMenu => true;
        public override bool IsExecuteOnClient => true;

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            bool noArg = _params == null || _params.Count == 0;
            string arg = noArg ? "status" : _params[0].ToLowerInvariant().Trim();
            string outLine;
            if (arg == "on" || arg == "1" || arg == "enable" || arg == "true")
            {
                DiagToggle.Set(true);
                outLine = "[7dtd-fastconnect] diag ON: verbose traces enabled (window/spawn/flags)";
            }
            else if (arg == "off" || arg == "0" || arg == "disable" || arg == "false")
            {
                DiagToggle.Set(false);
                outLine = "[7dtd-fastconnect] diag OFF: verbose traces muted";
            }
            else if (arg == "toggle" || arg == "flip")
            {
                bool next = !DiagToggle.Enabled;
                DiagToggle.Set(next);
                outLine = "[7dtd-fastconnect] diag " + (next ? "ON" : "OFF") + " (toggled)";
            }
            else if (arg == "status")
            {
                outLine = DiagToggle.StatusLine();
                if (noArg)
                    outLine += "\n" + getHelp();
            }
            else
            {
                // A typo must not read as a status query: say which word was
                // not understood, then the current state anyway.
                outLine = "[7dtd-fastconnect] diag: unknown argument '"
                    + LogText.EchoForMessage(_params[0])
                    + "'; expected on, off, toggle or status (no argument means status)\n"
                    + DiagToggle.StatusLine();
            }
            ConsoleOutput.Out(outLine);
        }
    }
}
