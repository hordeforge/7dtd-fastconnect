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
            Emit(line, logError: false);
        }

        /// <summary>
        /// Same echo, logged at error severity. A rejected target or a failed
        /// connect is a failure whether it was typed at the F1 console or
        /// driven by the auto-join path, and both callers already separate the
        /// two outcomes; logging the F1 one at info would leave the log
        /// claiming a clean run for a join that never happened.
        /// </summary>
        internal static void Fail(string line)
        {
            Emit(line, logError: true);
        }

        static void Emit(string line, bool logError)
        {
            // Console echo is best-effort: Output throws while the F1 console
            // is tearing down.
            try { SingletonMonoBehaviour<SdtdConsole>.Instance?.Output(line); }
            catch (Exception) { }
            // Guarded for the same reason: a Log call that throws (log
            // rollover, teardown) would otherwise escape into the stock
            // console-command dispatcher, and the line the harnesses grep for
            // would be lost with no other copy.
            try
            {
                if (logError) Log.Error(line);
                else Log.Out(line);
            }
            catch (Exception) { }
        }
    }
}
