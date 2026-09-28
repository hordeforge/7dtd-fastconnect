using System;

namespace SdtdConnect
{
    /// <summary>Single place an F1 console command writes its reply.</summary>
    internal static class ConsoleOutput
    {
        /// <summary>
        /// Echoes the line on screen and in the log. The log line is the record
        /// that matters: join harnesses grep the client log, not the console.
        /// </summary>
        internal static void Out(string line)
        {
            // Console echo is best-effort: Output throws while the F1 console
            // is tearing down.
            try { SingletonMonoBehaviour<SdtdConsole>.Instance?.Output(line); }
            catch (Exception) { }
            Log.Out(line);
        }
    }
}
