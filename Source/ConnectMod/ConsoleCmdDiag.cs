using System;
using System.Collections.Generic;

namespace SdtdConnect
{
    /// <summary>F1 console: diag on/off/toggle/status, toggles verbose traces at runtime.</summary>
    public class ConsoleCmdDiag : ConsoleCmdAbstract
    {
        public override string[] getCommands() => new[] { "diag", "7dtd_diag", "zdiag" };
        public override string getDescription() => "Toggle verbose 7dtd-fastconnect diagnostics (opt-in, off by default).";

        public override string getHelp() =>
            "diag on|off|status\n" +
            "  diag on      enable verbose traces\n" +
            "  diag off     disable verbose traces\n" +
            "  diag toggle  flip\n" +
            "  diag status  show current\n" +
            "Launch with 7DTD_CONNECT_DEBUG=1 for verbose on boot. Otherwise off by default.";

        public override bool AllowedInMainMenu => true;
        public override bool IsExecuteOnClient => true;

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            string arg = (_params != null && _params.Count > 0) ? _params[0].ToLowerInvariant().Trim() : "status";
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
            else // status and anything else
            {
                outLine = DiagToggle.StatusLine();
                if (_params == null || _params.Count == 0)
                    outLine += "\n" + getHelp();
            }
            // Console echo is best-effort: Output throws while the F1 console
            // is tearing down, and the Log.Out below is the record that
            // matters (harnesses grep the client log, not the console).
            try { SingletonMonoBehaviour<SdtdConsole>.Instance?.Output(outLine); }
            catch (Exception) { }
            Log.Out(outLine);
        }
    }
}
