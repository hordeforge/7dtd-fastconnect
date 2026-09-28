using System.Text;

namespace SdtdConnect
{
    /// <summary>
    /// Log-line hygiene shared by every module that echoes operator input.
    /// Leaf helper: env parsing, connect targets and console commands all
    /// depend on it, never the other way round.
    /// </summary>
    internal static class LogText
    {
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

        /// <summary>
        /// One-line echo of operator input for an error message: control
        /// characters flattened, long pastes cut so a mistyped paste cannot
        /// scroll the reason off screen.
        /// </summary>
        internal static string EchoForMessage(string value)
        {
            const int maxChars = 40;
            if (string.IsNullOrEmpty(value)) return value;
            string flat = SanitizeForLog(value).Trim();
            return flat.Length <= maxChars ? flat : flat.Substring(0, maxChars) + "...";
        }
    }
}
