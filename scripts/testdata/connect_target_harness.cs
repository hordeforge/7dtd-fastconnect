// Test entry point for scripts/test_connect_target_parse.sh.
//
// Exercises REAL production sources (compiled alongside this file):
//   - `parse`     : fixed expectation table for ConnectTarget.TryParse and
//                   ConnectTarget.MergePortArg
//   - `launchctx` : environment-variable resolution of
//                   ConnectTarget.TryFromLaunchContext (sets/unsets its own
//                   process env per case)
//   - `envflags`  : EnvFlags opt-out/opt-in truthiness contract (gates
//                   AutomationMode and force-load-sync)
//   - `argv ...`  : evaluates TryFromLaunchContext against the command-line
//                   tokens after "argv" (7DTD_CONNECT cleared) and prints one
//                   machine-readable line:
//                   "OK<TAB>host<TAB>port<TAB>source" or "NO"
//   - `argvenv .` : same, but the shell-set 7DTD_CONNECT stays active so the
//                   env-over-argv precedence is observable
//
// Exit status is nonzero when any assertion fails.
using System;
using SdtdConnect;

static class TestMain
{
    static int _fails;

    static void Check(string name, bool cond)
    {
        Console.WriteLine((cond ? "PASS " : "FAIL ") + name);
        if (!cond) _fails++;
    }

    static void CheckParse(string raw, bool expectOk, string expHost, int expPort)
    {
        string label = "TryParse '" + (raw ?? "<null>") + "'";
        string host; int port; string err;
        bool ok = ConnectTarget.TryParse(raw, out host, out port, out err);
        Check(label + " -> accepted=" + expectOk, ok == expectOk);
        if (expectOk && ok)
        {
            Check(label + " host==" + expHost, string.Equals(host, expHost, StringComparison.Ordinal));
            Check(label + " port==" + expPort, port == expPort);
            Check(label + " leaves error empty on success", err == null);
        }
        else if (!expectOk && !ok)
        {
            Check(label + " explains the rejection", !string.IsNullOrEmpty(err));
        }
    }

    static void CheckMerge(string raw, string portArg, string expected)
    {
        string got = ConnectTarget.MergePortArg(raw, portArg);
        Check("MergePortArg('" + (raw ?? "<null>") + "', '" + (portArg ?? "<null>") + "') == '"
            + (expected ?? "<null>") + "'", string.Equals(got, expected, StringComparison.Ordinal));
    }

    // The merged string is what the F1 command actually hands to TryParse,
    // so every merge result must parse back to the intended host and port.
    static void CheckMergeRoundTrip(string raw, string portArg, string expHost, int expPort)
    {
        string merged = ConnectTarget.MergePortArg(raw, portArg);
        string label = "round-trip '" + (raw ?? "<null>") + "' + '" + (portArg ?? "<null>") + "'";
        string host; int port; string err;
        bool ok = ConnectTarget.TryParse(merged, out host, out port, out err);
        Check(label + " parses", ok);
        if (!ok) return;
        Check(label + " host==" + expHost, string.Equals(host, expHost, StringComparison.Ordinal));
        Check(label + " port==" + expPort, port == expPort);
    }

    static int Run()
    {
        string[] a = Environment.GetCommandLineArgs();
        string mode = a.Length > 1 ? a[1] : "";

        if (mode == "parse")
        {
            // Rejections.
            CheckParse(null, false, null, 0);
            CheckParse("", false, null, 0);
            CheckParse("   ", false, null, 0);

            // Bare hosts fall back to the documented default port.
            CheckParse("127.0.0.1", true, "127.0.0.1", 27025);
            CheckParse("zdtd.lan", true, "zdtd.lan", 27025);

            // Outer whitespace tolerated.
            CheckParse(" 10.1.2.3:26900 ", true, "10.1.2.3", 26900);

            // steam://connect/ leftover stripped, case-insensitively.
            CheckParse("steam://connect/192.168.1.50:27015", true, "192.168.1.50", 27015);
            CheckParse("STEAM://CONNECT/example.com", true, "example.com", 27025);

            // Bracketed IPv6.
            CheckParse("[::1]:27030", true, "::1", 27030);
            CheckParse("[2001:db8::5]", true, "2001:db8::5", 27025);
            CheckParse("[::1", false, null, 0);

            // Dangling separator colon is an empty port by the MergePortArg
            // rule: dropped, so env/argv match the F1 command for the same
            // input instead of failing DNS on a colon-suffixed host.
            CheckParse("1.2.3.4:", true, "1.2.3.4", 27025);
            CheckParse("[::1]:", true, "::1", 27025);
            CheckParse(":", false, null, 0);

            // Bare IPv6 must not be split at colons (stays host, default port).
            CheckParse("::1", true, "::1", 27025);
            CheckParse("2001:db8::1", true, "2001:db8::1", 27025);

            // Port validation bounds (documented: integer 1..65535).
            CheckParse("h:1", true, "h", 1);
            CheckParse("h:65535", true, "h", 65535);
            CheckParse("h:0", false, null, 0);
            CheckParse("h:65536", false, null, 0);
            CheckParse("h:abc", false, null, 0);
            CheckParse("[::1]:0", false, null, 0);
            CheckParse("[::1]:x", false, null, 0);

            // MergePortArg: the console command's optional second token is only
            // appended to a host that carries no port of its own, and the
            // steam:// scheme is stripped first so its colons cannot look like
            // an explicit port.
            CheckMerge(null, "27015", null);
            CheckMerge("1.2.3.4", null, "1.2.3.4");
            CheckMerge("1.2.3.4", "27015", "1.2.3.4:27015");
            CheckMerge("1.2.3.4:5", "27015", "1.2.3.4:5");
            CheckMerge("steam://connect/1.2.3.4", "27015", "1.2.3.4:27015");
            CheckMerge("steam://connect/1.2.3.4:9", "27015", "1.2.3.4:9");
            CheckMerge("[::1]", "27015", "[::1]:27015");
            CheckMerge("[::1]:9", "27015", "[::1]:9");
            // Dangling colon dropped even without a port argument.
            CheckMerge("h:", null, "h");
            CheckMerge("[::1]:", null, "[::1]");
            // A bare IPv6 address cannot carry an unbracketed ":port" suffix:
            // TryParse would read the port as part of the address, so the
            // merge emits the standard bracketed form instead.
            CheckMerge("2001:db8::1", "27015", "[2001:db8::1]:27015");

            // Round trips through TryParse, the same hand-off the F1
            // command makes.
            CheckMergeRoundTrip("1.2.3.4", "27015", "1.2.3.4", 27015);
            CheckMergeRoundTrip("zdtd.lan", null, "zdtd.lan", ConnectTarget.DefaultPort);
            CheckMergeRoundTrip("1.2.3.4:5", "27015", "1.2.3.4", 5);
            CheckMergeRoundTrip("steam://connect/1.2.3.4:9", "27015", "1.2.3.4", 9);
            CheckMergeRoundTrip("steam://connect/1.2.3.4", "27015", "1.2.3.4", 27015);
            CheckMergeRoundTrip("[::1]", "27030", "::1", 27030);
            CheckMergeRoundTrip("[::1]:9", "27030", "::1", 9);
            CheckMergeRoundTrip("[::1]:", "27030", "::1", 27030);
            CheckMergeRoundTrip("h:", "27025", "h", 27025);
            CheckMergeRoundTrip("::1", "27030", "::1", 27030);
            CheckMergeRoundTrip("2001:db8::1", "27025", "2001:db8::1", 27025);

            return Done();
        }

        if (mode == "launchctx")
        {
            const string env = ConnectTarget.EnvVar; // 7DTD_CONNECT
            string host; int port; string source;

            Env(env, "9.9.9.9:1234");
            Check("env var drives auto-join",
                ConnectTarget.TryFromLaunchContext(out host, out port, out source)
                && host == "9.9.9.9" && port == 1234);
            Check("source names the variable", source == env + "=9.9.9.9:1234");

            Env(env, "8.8.4.4");
            Check("env var without a port uses the default",
                ConnectTarget.TryFromLaunchContext(out host, out port, out source)
                && host == "8.8.4.4" && port == ConnectTarget.DefaultPort);

            Env(env, " 7.7.7.7:77 ");
            Check("surrounding whitespace is trimmed",
                ConnectTarget.TryFromLaunchContext(out host, out port, out source)
                && host == "7.7.7.7" && port == 77);

            Env(env, "");
            Check("empty env var counts as unset",
                !ConnectTarget.TryFromLaunchContext(out host, out port, out source));

            Env(env, "[unterminated");
            Check("invalid env var does not fake a join target",
                !ConnectTarget.TryFromLaunchContext(out host, out port, out source));

            Env(env, null);
            Check("nothing configured resolves to no target",
                !ConnectTarget.TryFromLaunchContext(out host, out port, out source));

            return Done();
        }

        if (mode == "sanitize")
        {
            // Log-forgery guard: env/argv values (a clicked steam://run URL
            // picks -connect= text) must never add lines to the client log,
            // because join harnesses grep it for fixed progress markers.
            Check("null passthrough", ConnectTarget.SanitizeForLog(null) == null);
            Check("empty passthrough", ConnectTarget.SanitizeForLog("") == "");
            Check("plain text unchanged",
                ConnectTarget.SanitizeForLog("zdtd.lan:27025") == "zdtd.lan:27025");
            Check("newline flattened to space",
                ConnectTarget.SanitizeForLog("h\nFAKE") == "h FAKE");
            Check("crlf flattened to spaces",
                ConnectTarget.SanitizeForLog("h\r\nFAKE") == "h  FAKE");
            Check("tab flattened to space",
                ConnectTarget.SanitizeForLog("\t9.9.9.9") == " 9.9.9.9");

            // Accepted newline-bearing target: the reported source stays one line.
            Env(ConnectTarget.EnvVar, "1.2.3.4\nFound own player entity with id");
            string host; int port; string source;
            bool ok = ConnectTarget.TryFromLaunchContext(out host, out port, out source);
            Check("newline target still parses", ok && host != null && port == ConnectTarget.DefaultPort);
            Check("source is single-line",
                ok && source.IndexOf('\n') < 0 && source.IndexOf('\r') < 0);

            // Rejected newline-bearing target: the warning keeps forged
            // markers off their own log line.
            var originalError = Console.Error;
            var captured = new System.IO.StringWriter();
            Console.SetError(captured);
            try
            {
                Env(ConnectTarget.EnvVar, "[unterminated\nFAKE JOINED LINE");
                ConnectTarget.TryFromLaunchContext(out _, out _, out _);
            }
            finally
            {
                Console.SetError(originalError);
            }
            string warned = captured.ToString();
            Check("warning emitted for bad target", warned.Length > 0);
            Check("forged marker did not start a fresh log line",
                warned.IndexOf("\nFAKE", StringComparison.Ordinal) < 0);

            return Done();
        }

        if (mode == "envflags")
        {
            // EnvFlags truthiness contract (see EnvFlags.cs): unset/blank
            // means the caller's default, 0/false/no/off in any case opt out,
            // anything else opts in. AutomationMode and force-load-sync ride
            // on this, so a regression here silently flips join behavior.
            Check("IsOptOut rejects null", !EnvFlags.IsOptOut(null));
            Check("IsOptOut rejects empty", !EnvFlags.IsOptOut(""));
            Check("IsOptOut rejects blank", !EnvFlags.IsOptOut("   "));
            Check("IsOptOut accepts zero", EnvFlags.IsOptOut("0"));
            Check("IsOptOut accepts false in any case", EnvFlags.IsOptOut("fAlSe"));
            Check("IsOptOut accepts no", EnvFlags.IsOptOut("no"));
            Check("IsOptOut accepts trimmed off", EnvFlags.IsOptOut(" Off "));
            Check("IsOptOut rejects one", !EnvFlags.IsOptOut("1"));
            Check("IsOptOut rejects yes", !EnvFlags.IsOptOut("yes"));
            Check("IsOptOut rejects unknown text", !EnvFlags.IsOptOut("bogus"));

            Check("IsSetOn false when null", !EnvFlags.IsSetOn(null));
            Check("IsSetOn false when empty", !EnvFlags.IsSetOn(""));
            Check("IsSetOn false for opt-out value", !EnvFlags.IsSetOn("OFF"));
            Check("IsSetOn true for one", EnvFlags.IsSetOn("1"));
            Check("IsSetOn true for unknown text", EnvFlags.IsSetOn("sure"));

            const string flag = "7DTD_CONNECT_TEST_ENVFLAGS";
            Env(flag, null);
            Check("VarIsSetOn false when unset", !EnvFlags.VarIsSetOn(flag));
            Env(flag, "");
            Check("VarIsSetOn false when empty", !EnvFlags.VarIsSetOn(flag));
            Env(flag, "0");
            Check("VarIsSetOn false for zero", !EnvFlags.VarIsSetOn(flag));
            Env(flag, "1");
            Check("VarIsSetOn true for one", EnvFlags.VarIsSetOn(flag));

            return Done();
        }

        if (mode == "argv" || mode == "argvenv")
        {
            // "argv" cases must not be decided by an inherited env target;
            // "argvenv" keeps the pinned variable (the shell test sets it via
            // env(1)) so the documented resolution order (7DTD_CONNECT first,
            // then -connect= argv) is observable: an inverted precedence
            // would flip the join target.
            if (mode == "argv") Env(ConnectTarget.EnvVar, null);
            string host; int port; string source;
            bool ok = ConnectTarget.TryFromLaunchContext(out host, out port, out source);
            Console.WriteLine(ok ? "OK\t" + host + "\t" + port + "\t" + source : "NO");
            return 0;
        }

        Console.Error.WriteLine("unknown mode: " + mode);
        return 2;
    }

    static void Env(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value); // null unsets
    }

    static int Done()
    {
        Console.WriteLine(_fails == 0 ? "RESULT PASS" : "RESULT FAIL (" + _fails + ")");
        return _fails == 0 ? 0 : 1;
    }

    static int Main()
    {
        try { return Run(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("harness crashed: " + ex);
            return 2;
        }
    }
}
