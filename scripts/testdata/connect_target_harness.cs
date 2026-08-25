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
//   - `playernames`: PlayerNames.Resolve invariants (server kicks empty or
//                   duplicate names: resolved identity must be non-empty,
//                   trimmed, and within the stock client-name cap)
//   - `forcesync` : BootUnblock force-load-sync contract (default-on,
//                   opt-out honored once-logged, env decision snapshotted)
//   - `automation ...`: AutomationMode.Enabled decision table, one process per
//                   case (static-readonly detection): unset resolves from the
//                   launch context, explicit values ride EnvFlags truthiness,
//                   and an explicit opt-out beats a detected target
//   - `connectready`: ConnectReady.IsReady gate state machine driven by a
//                   manually advanced monotonic clock: gate chain order,
//                   bounded cross-user wait measured from FIRST null-id
//                   sighting, one expiry note per episode (the poll loop must
//                   not flood the log join harnesses grep), reset-for-rejoin
//   - `argv ...`  : evaluates TryFromLaunchContext against the command-line
//                   tokens after "argv" (7DTD_CONNECT cleared) and prints one
//                   machine-readable line:
//                   "OK<TAB>host<TAB>port<TAB>source" or "NO"
//   - `argvenv .` : same, but the shell-set 7DTD_CONNECT stays active so the
//                   env-over-argv precedence is observable
//   - `fuzz`      : deterministic seeded generator (grammar-biased: brackets,
//                   colons, scheme prefixes, port-bound numerics, control
//                   chars) hammers TryParse / MergePortArg / SanitizeForLog /
//                   TryFromLaunchContext and asserts invariants instead of
//                   fixed expectations: totality (no throw), accept/reject
//                   contracts (bounded port, non-empty trimmed host, error
//                   text, null host on reject), log flattening (length and
//                   control-char free), cross-port merge consistency (the
//                   same host must parse for every valid appended port), and
//                   single-line source through the env wrapper. The seed is
//                   printed so any failure reproduces offline.
//
// Exit status is nonzero when any assertion fails.
using System;
using System.Text;
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

    sealed class FakeUser : SdtdConnect.Platform.IUser { }

    static readonly System.Reflection.BindingFlags BootStatic =
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;

    // BootUnblock caches its env decision and its one-shot state in statics;
    // reset them so every forcesync case starts from a fresh process state.
    static void ResetBootUnblock()
    {
        typeof(BootUnblock).GetField("_forceSyncSet", BootStatic).SetValue(null, false);
        typeof(BootUnblock).GetField("_forceSyncOptOutLogged", BootStatic).SetValue(null, false);
        typeof(BootUnblock).GetField("_forceSyncEnabled", BootStatic).SetValue(null, null);
        LoadManager.forceLoadSync = false;
    }

    static readonly System.Reflection.FieldInfo CrossWaitStartField =
        typeof(ConnectReady).GetField("_crossWaitStart",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    static readonly System.Reflection.FieldInfo CrossProceedLoggedField =
        typeof(ConnectReady).GetField("_crossProceedLogged",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    static readonly System.Reflection.BindingFlags NativeProceedLoggedFlags =
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
    static readonly System.Reflection.FieldInfo NativeProceedLoggedField =
        typeof(ConnectReady).GetField("_nativeProceedLogged", NativeProceedLoggedFlags);

    static float CrossWaitStart
    {
        get { return (float)CrossWaitStartField.GetValue(null); }
        set { CrossWaitStartField.SetValue(null, value); }
    }

    static bool Ready(out string reason)
    {
        return ConnectReady.IsReady(out reason);
    }

    // Returns everything written to stderr (the Log stub) while running body.
    static string CaptureStderr(Action body)
    {
        var originalError = Console.Error;
        var captured = new System.IO.StringWriter();
        Console.SetError(captured);
        try { body(); }
        finally { Console.SetError(originalError); }
        return captured.ToString();
    }

    static int CountOccurrences(string text, string marker)
    {
        int n = 0, idx = text.IndexOf(marker, StringComparison.Ordinal);
        while (idx >= 0)
        {
            n++;
            idx = text.IndexOf(marker, idx + marker.Length, StringComparison.Ordinal);
        }
        return n;
    }

    static int RunConnectReady()
    {
        const string crossNote = "past wait window, proceeding anyway";
        const string nativeNote = "past boot window, proceeding anyway";

        // Fresh gate state and a deterministic monotonic clock per run.
        CrossWaitStart = -1f;
        CrossProceedLoggedField.SetValue(null, false);
        NativeProceedLoggedField.SetValue(null, false);
        UnityEngine.Time.unscaledTime = 0f;

        string reason;

        // Gate chain order: each missing prerequisite names itself.
        Check("no game manager -> not ready", !Ready(out reason) && reason == "staticData=false");
        GameManager.Instance = new GameManager();
        Check("static data pending -> not ready", !Ready(out reason) && reason == "staticData=false");
        GameManager.Instance.bStaticDataLoaded = true;
        Check("no connection manager -> not ready", !Ready(out reason) && reason == "ConnectionManager=null");
        var cmGate = new ConnectionManager();
        SingletonMonoBehaviour<ConnectionManager>.Instance = cmGate;
        // A live session must short-circuit the gate by name: the auto-join
        // poll re-runs IsReady, and without this branch a second join attempt
        // would fire against an established connection.
        cmGate.IsConnected = true;
        Check("already connected -> gate names it", !Ready(out reason) && reason == "already-connected");
        cmGate.IsConnected = false;
        Check("no native platform -> not ready", !Ready(out reason) && reason == "NativePlatform=null");

        SdtdConnect.Platform.PlatformManager.NativePlatform = new SdtdConnect.Platform.PlatformManager();
        PermissionsManager.IsMultiplayerAllowed = () => true;
        Check("all prerequisites met with no platform users -> ready",
            Ready(out reason) && reason == null);

        // Native steam identity is optional after the boot window only.
        SdtdConnect.Platform.PlatformManager.NativePlatform.User = new FakeUser();
        UnityEngine.Time.unscaledTime = 10f;
        Check("native user id null inside boot window -> blocked early",
            !Ready(out reason) && reason.IndexOf("early", StringComparison.Ordinal) >= 0);

        // The bounded cross-user wait starts at FIRST null-id sighting.
        var cross = new SdtdConnect.Platform.PlatformManager();
        cross.User = new FakeUser();
        SdtdConnect.Platform.PlatformManager.CrossplatformPlatform = cross;
        Check("cross wait engages at first sighting",
            !Ready(out reason) && reason == "cross user not logged in yet" && CrossWaitStart == 10f);

        UnityEngine.Time.unscaledTime = 39f; // 29s elapsed, inside the window
        Check("cross wait still holds near the end of its window",
            !Ready(out reason) && reason == "cross user not logged in yet");

        // Past the window the gate proceeds so a broken EOS login cannot pin
        // the join forever - and it says so exactly once across repeated polls.
        UnityEngine.Time.unscaledTime = 41f;
        bool proceededPastWindow = false;
        int crossNotes, nativeNotes;
        string pollLog = CaptureStderr(delegate
        {
            proceededPastWindow = Ready(out reason);
            Ready(out reason);
            Ready(out reason);
        });
        crossNotes = CountOccurrences(pollLog, crossNote);
        nativeNotes = CountOccurrences(pollLog, nativeNote);
        Check("gate proceeds past the cross-user window", proceededPastWindow && reason == null);
        Check("cross expiry note logged once across polls", crossNotes == 1);
        Check("native expiry note logged once across polls", nativeNotes == 1);

        // Login completes: episode resets so a later logout/relogin waits anew.
        ((FakeUser)cross.User).PlatformUserId = "76561197960265728";
        ((FakeUser)SdtdConnect.Platform.PlatformManager.NativePlatform.User).PlatformUserId = "76561197960265729";
        Ready(out reason);
        Check("login completion resets the cross-wait episode",
            CrossWaitStart < 0f
            && !(bool)CrossProceedLoggedField.GetValue(null));

        ((FakeUser)cross.User).PlatformUserId = null;
        UnityEngine.Time.unscaledTime = 50f;
        Check("a fresh null-id episode waits again instead of proceeding instantly",
            !Ready(out reason) && reason == "cross user not logged in yet" && CrossWaitStart == 50f);

        return Done();
    }

    static int Run()
    {
        string[] a = Environment.GetCommandLineArgs();
        string mode = a.Length > 1 ? a[1] : "";

        if (mode == "connectready")
        {
            return RunConnectReady();
        }

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

            // A lone leading colon is an empty host before a port; it must be
            // rejected at parse time rather than deferring to DNS with the
            // caller's port silently reset to the default.
            CheckParse(":27025", false, null, 0);
            CheckParse(":abc", false, null, 0);
            // Doubled dangling colons are the same mistake as one.
            CheckParse("h::", true, "h", 27025);

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
            CheckMerge("h::", null, "h");
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
            // The launch context is re-read on every menu open / boot probe,
            // so an invalid value must warn once per process, not once per
            // read: repeated warnings would flood the client log that join
            // harnesses grep for fixed markers.
            string warnLog = CaptureStderr(delegate
            {
                Check("invalid env var does not fake a join target",
                    !ConnectTarget.TryFromLaunchContext(out host, out port, out source));
                ConnectTarget.TryFromLaunchContext(out host, out port, out source);
            });
            Check("invalid target warns exactly once per process",
                CountOccurrences(warnLog, "ignored:") == 1);
            Check("warning tells the reader auto-join is off",
                warnLog.IndexOf("auto-join disabled", StringComparison.Ordinal) >= 0);

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

        if (mode == "forcesync")
        {
            // BootUnblock's force-load-sync contract: default-on for
            // automation, opt-out honored, and the env decision snapshotted on
            // first use because hooks re-check every frame.
            const string env = BootUnblock.ForceLoadSyncEnv;

            ResetBootUnblock();
            Env(env, null);
            Check("force-load-sync defaults to enabled when unset",
                BootUnblock.ForceLoadSyncEnabled());
            BootUnblock.ApplyForceLoadSync();
            Check("apply flips LoadManager.forceLoadSync when enabled",
                LoadManager.forceLoadSync);

            ResetBootUnblock();
            Env(env, "0");
            Check("explicit zero opts out", !BootUnblock.ForceLoadSyncEnabled());
            string optOutLog = CaptureStderr(delegate
            {
                BootUnblock.ApplyForceLoadSync();
                BootUnblock.ApplyForceLoadSync();
            });
            Check("opt-out leaves LoadManager.forceLoadSync untouched",
                !LoadManager.forceLoadSync);
            Check("opt-out note logged once across repeated applies",
                CountOccurrences(optOutLog, "disabled by") == 1);

            ResetBootUnblock();
            Env(env, "1");
            Check("enabled decision cached", BootUnblock.ForceLoadSyncEnabled());
            Env(env, "0");
            Check("env change after first read does not flip the snapshot",
                BootUnblock.ForceLoadSyncEnabled());

            return Done();
        }

        if (mode == "playernames")
        {
            // Stock dedi kicks "Empty name or player ID" for loopback joins
            // when Steam is offline, and rejects duplicate names: whatever the
            // host environment holds, Resolve must return a usable identity.
            string name = PlayerNames.Resolve();
            Check("resolved name is never empty", !string.IsNullOrEmpty(name));
            Check("resolved name fits the stock client-name cap",
                name.Length <= PlayerNames.MaxLength);
            Check("resolved name carries no outer whitespace", name == name.Trim());

            return Done();
        }

        if (mode == "automation")
        {
            // Decision table for the gate every automation patch hangs on.
            // Detection is static-readonly per process, so each case runs as
            // its own process; tokens after the mode configure the context:
            //   conn          set 7DTD_CONNECT (a detected launch target)
            //   auto=<value>  set 7DTD_CONNECT_AUTOMATION
            Env(ConnectTarget.EnvVar, null);
            Env(AutomationMode.EnvVar, null);
            foreach (string token in a)
            {
                if (token == "conn") Env(ConnectTarget.EnvVar, "5.6.7.8:99");
                else if (token.StartsWith("auto=", StringComparison.Ordinal))
                    Env(AutomationMode.EnvVar, token.Substring("auto=".Length));
            }
            Console.WriteLine(AutomationMode.Enabled ? "ON" : "OFF");
            return 0;
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

        if (mode == "fuzz")
        {
            return RunFuzz();
        }

        Console.Error.WriteLine("unknown mode: " + mode);
        return 2;
    }

    // ------------------------------------------------------------------
    // Fuzz target for the launch-target grammar (the mod's untrusted-input
    // surface: env/argv values are attacker-shapable via steam://run URLs).
    // Deterministic seed so any invariant violation reproduces offline; a
    // fuzzer alone proves presence of bugs, so every generated input is also
    // checked against invariants that must hold for ALL inputs.
    // ------------------------------------------------------------------
    const int FuzzSeed = 20260826;
    static int _fuzzReported;

    // Failure reporting with a cap: the same bug usually fires thousands of
    // times; the first few inputs plus the printed seed are enough to triage.
    static void CheckFuzz(string name, bool cond)
    {
        if (cond) return;
        _fails++;
        if (_fuzzReported < 20)
        {
            _fuzzReported++;
            Console.WriteLine("FAIL " + name);
        }
    }

    static string EscapeForLog(string s)
    {
        if (s == null) return "<null>";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c == '\\') sb.Append("\\\\");
            else if (c < 0x20 || c == 0x7f) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static readonly string[] FuzzPorts =
    {
        "0", "00", "1", "65535", "65536", "27025", "+1", "-1", " 42",
        "2147483647", "2147483648", "99999999999999999999", "0x1b",
        "abc", "", " ", "\t7"
    };

    static readonly string[] FuzzHosts =
    {
        "127.0.0.1", "10.0.0.1", "zdtd.lan", "localhost", "1.2.3.4",
        "::1", "2001:db8::1", "", "[", "]", "[::1]", "[::1"
    };

    static string RandText(Random r, string charset, int len)
    {
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append(charset[r.Next(charset.Length)]);
        return sb.ToString();
    }

    static string RandV6(Random r)
    {
        int groups = r.Next(2, 9);
        var sb = new StringBuilder();
        for (int i = 0; i < groups; i++)
        {
            if (i > 0) sb.Append(':');
            if (r.Next(6) == 0) { sb.Append(':'); continue; } // splice in "::" forms
            sb.Append(RandText(r, "0123456789abcdefABCDEF", r.Next(0, 5)));
        }
        return sb.ToString();
    }

    static string GenInput(Random r, int i)
    {
        const string grammar = "[]:.0123456789abcdefABCDEFghijklmnopqrstuvwxyz /=-+\t\n\r";
        string raw;
        switch (i % 6)
        {
            case 0:
                raw = RandText(r, grammar, r.Next(0, 49));
                break;
            case 1:
                raw = (r.Next(2) == 0 ? "steam://connect/" : "STEAM://CONNECT/")
                    + RandText(r, grammar, r.Next(0, 25));
                break;
            case 2:
                raw = "[" + RandV6(r) + "]";
                if (r.Next(2) == 0) raw += ":" + FuzzPorts[r.Next(FuzzPorts.Length)];
                if (r.Next(8) == 0) raw += RandText(r, "[:]", r.Next(0, 3));
                break;
            case 3:
                raw = FuzzHosts[r.Next(FuzzHosts.Length)] + ":" + FuzzPorts[r.Next(FuzzPorts.Length)];
                break;
            case 4:
                raw = RandText(r, "ab19.:]", r.Next(0, 6)) + new string(':', r.Next(1, 5))
                    + RandText(r, "ab19.:]", r.Next(0, 6));
                break;
            default:
                raw = RandV6(r);
                if (r.Next(3) == 0) raw = "[" + raw + "]" + ":" + FuzzPorts[r.Next(FuzzPorts.Length)];
                break;
        }
        // Occasionally forge log markers or pad: control chars must never
        // reach the log as line breaks, and outer whitespace must not change
        // the parse outcome.
        if (r.Next(4) == 0) raw = "\n" + raw;
        if (r.Next(4) == 0) raw = raw + "\rFAKE JOINED LINE";
        if (r.Next(6) == 0) raw = "  " + raw + " ";
        return raw;
    }

    static void FuzzOne(string raw, int i)
    {
        string label = "raw='" + EscapeForLog(raw) + "'";

        // Totality: none of the entry points may throw on any input.
        string san;
        try { san = ConnectTarget.SanitizeForLog(raw); }
        catch (Exception ex) { CheckFuzz(label + " SanitizeForLog threw", false); Console.WriteLine("     " + ex.GetType().Name); return; }

        if (san != null)
        {
            CheckFuzz(label + " sanitize preserves length", san.Length == (raw ?? "").Length);
            bool clean = true;
            foreach (char c in san) { if (char.IsControl(c)) { clean = false; break; } }
            CheckFuzz(label + " sanitize strips control chars", clean);
        }

        string host; int port; string err;
        bool ok;
        try { ok = ConnectTarget.TryParse(raw, out host, out port, out err); }
        catch (Exception ex) { CheckFuzz(label + " TryParse threw", false); Console.WriteLine("     " + ex.GetType().Name); return; }

        if (ok)
        {
            CheckFuzz(label + " accepted host non-empty", host != null && host.Length > 0);
            CheckFuzz(label + " accepted host trimmed", host == null || host == host.Trim());
            CheckFuzz(label + " accepted port bounded", port >= 1 && port <= 65535);
            CheckFuzz(label + " accept leaves no error", err == null);
            // Outer padding must never change the verdict or the values.
            string paddedHost; int paddedPort; string paddedErr;
            bool paddedOk = ConnectTarget.TryParse("  " + raw + " ", out paddedHost, out paddedPort, out paddedErr);
            CheckFuzz(label + " padding keeps verdict", paddedOk
                && paddedPort == port && paddedHost == host);
        }
        else
        {
            CheckFuzz(label + " rejection explains itself", !string.IsNullOrEmpty(err));
            CheckFuzz(label + " rejection leaves host null", host == null);
        }

        // A raw value without any colon cannot carry a port suffix (the
        // steam:// scheme and bare IPv6 both contain colons), so an
        // accepted parse of it must fall back to the documented default.
        if (ok && raw.IndexOf(':') < 0)
            CheckFuzz(label + " portless input keeps default port", port == ConnectTarget.DefaultPort);

        // Cross-port merge consistency: MergePortArg may add exactly one
        // ":<digits>" suffix, so every valid appended port must yield the
        // same accept/reject verdict and the same host.
        const int portA = 27025, portB = 1, portC = 65535;
        string mergedA = ConnectTarget.MergePortArg(raw, portA.ToString());
        string mergedB = ConnectTarget.MergePortArg(raw, portB.ToString());
        string mergedC = ConnectTarget.MergePortArg(raw, portC.ToString());
        string mAHost, mBHost, mCHost; int mAPort, mBPort, mCPort; string mErr;
        try
        {
            bool okA = ConnectTarget.TryParse(mergedA, out mAHost, out mAPort, out mErr);
            bool okB = ConnectTarget.TryParse(mergedB, out mBHost, out mBPort, out mErr);
            bool okC = ConnectTarget.TryParse(mergedC, out mCHost, out mCPort, out mErr);
            CheckFuzz(label + " merge verdict stable across ports", okA == okB && okB == okC);
            if (okA && okB && okC)
                CheckFuzz(label + " merge host stable across ports",
                    mAHost == mBHost && mBHost == mCHost);
        }
        catch (Exception ex)
        {
            CheckFuzz(label + " merged TryParse threw", false);
            Console.WriteLine("     " + ex.GetType().Name);
        }

        // Sample the env wrapper too: it is what actually consumes
        // attacker-shapable bytes, and its reported source stays one line.
        if ((i % 64) == 0)
        {
            try
            {
                Environment.SetEnvironmentVariable(ConnectTarget.EnvVar, raw);
                string srcHost; int srcPort; string srcSource;
                bool srcOk = ConnectTarget.TryFromLaunchContext(out srcHost, out srcPort, out srcSource);
                if (srcOk)
                {
                    CheckFuzz(label + " env source single-line",
                        srcSource.IndexOf('\n') < 0 && srcSource.IndexOf('\r') < 0);
                    CheckFuzz(label + " env port bounded", srcPort >= 1 && srcPort <= 65535);
                    CheckFuzz(label + " env host non-empty", !string.IsNullOrEmpty(srcHost));
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(ConnectTarget.EnvVar, null);
            }
        }
    }

    static int RunFuzz()
    {
        const int iterations = 24000;
        var rng = new Random(FuzzSeed);
        int before = _fails;
        for (int i = 0; i < iterations; i++)
            FuzzOne(GenInput(rng, i), i);
        int found = _fails - before;
        Console.WriteLine("fuzz: seed=" + FuzzSeed + " iterations=" + iterations
            + " violations=" + found);
        return Done();
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
