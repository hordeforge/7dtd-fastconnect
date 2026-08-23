// Test entry point for scripts/test_connect_target_parse.sh.
//
// Exercises the REAL production ConnectTarget (compiled alongside this file):
//   - `parse`     : fixed expectation table for ConnectTarget.TryParse and
//                   ConnectTarget.MergePortArg
//   - `launchctx` : environment-variable resolution of
//                   ConnectTarget.TryFromLaunchContext (sets/unsets its own
//                   process env per case)
//   - `argv ...`  : evaluates TryFromLaunchContext against the command-line
//                   tokens after "argv" and prints one machine-readable line:
//                   "OK<TAB>host<TAB>port<TAB>source" or "NO"
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
            // Bare IPv6 has several colons, so none of them is an explicit port.
            CheckMerge("2001:db8::1", "27015", "2001:db8::1:27015");

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

        if (mode == "argv")
        {
            // argv cases must not be decided by an inherited env target.
            Env(ConnectTarget.EnvVar, null);
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
