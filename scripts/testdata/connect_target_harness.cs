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
//   - `probefailure`: ProbeFailure announce-once latch, keyed per probe name
//                   (once per name, a dead probe must not mute another, and
//                   an empty reason must not consume the latch)
//   - `forcesync` : BootUnblock force-load-sync contract (default-on,
//                   opt-out honored once-logged, env decision snapshotted)
//   - `automation ...`: AutomationMode.Enabled decision table, one process per
//                   case (static-readonly detection): unset resolves from the
//                   launch context, explicit values ride EnvFlags truthiness,
//                   and an explicit opt-out beats a detected target
//   - `connectrequest`: ConnectTarget.TryConnect one-attempt-at-a-time latch:
//                   a repeat request while an attempt is dialling is refused
//                   before it reaches the client, and the latch releases for a
//                   real retry (observed connection, left the session, or an
//                   attempt that never reported back outliving its window)
//   - `lookupwait`: ConnectTarget's bounded DNS wait. A lookup that gives up
//                   inside the window is still running, so its wait handle
//                   must stay open for the thread that completes it; a lookup
//                   that finishes in the window reports success and releases
//                   its handle
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
//   - `fuzz-text` : the same generator shape over the identity path
//                   (TextUtil code-point counting / NFC / pair-safe
//                   truncation, PlayerNames.Normalize and Cap,
//                   LogText.EchoForMessage, EnvFlags truthiness): an
//                   unpaired-surrogate and combining-mark generator asserting
//                   the cap contract (within the stock limit, a prefix of the
//                   input, never half a pair, idempotent) against an
//                   independent code-point count, so a name that reached the
//                   server under a spelling the operator never typed is a
//                   failure, not a surprise.
//
//   - `console`  : the F1 console commands driven through their real
//                   Execute() entry point against a recording SdtdConsole:
//                   what a player is told for no arguments, a rejected
//                   target, a dropped extra token, a dial that never lands
//                   and one that does, plus the diag state machine
// Exit status is nonzero when any assertion fails.
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
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

    // The F1 console shows nothing but the rejection text, so it has to name
    // either the offending value or the shape the operator should type.
    static void CheckRejectionMessage(string raw, params string[] mustMention)
    {
        string host; int port; string err;
        string label = "rejection of '" + (raw ?? "<null>") + "'";
        Check(label + " happens", !ConnectTarget.TryParse(raw, out host, out port, out err));
        if (err == null) return;
        foreach (string needle in mustMention)
            Check(label + " mentions '" + needle + "'", err.Contains(needle));
        Check(label + " stays on one line", err.IndexOf('\n') < 0);
    }

    sealed class FakeUser : SdtdConnect.Platform.IUser { }
    static readonly System.Reflection.BindingFlags PrivateStatic =
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;

    // BootUnblock caches its env decision and its one-shot state in statics;
    // reset them so every forcesync case starts from a fresh process state.
    static void ResetBootUnblock()
    {
        typeof(BootUnblock).GetField("_forceSyncSet", PrivateStatic).SetValue(null, false);
        typeof(BootUnblock).GetField("_forceSyncOptOutLogged", PrivateStatic).SetValue(null, false);
        typeof(BootUnblock).GetField("_forceSyncEnabled", PrivateStatic).SetValue(null, null);
        LoadManager.forceLoadSync = false;
    }

    static readonly System.Reflection.FieldInfo CrossWaitStartField = ConnectReadyField("_crossWaitStart");
    static readonly System.Reflection.FieldInfo CrossProceedLoggedField = ConnectReadyField("_crossProceedLogged");
    static readonly System.Reflection.FieldInfo NativeProceedLoggedField = ConnectReadyField("_nativeProceedLogged");

    static System.Reflection.FieldInfo ConnectReadyField(string name)
    {
        return typeof(ConnectReady).GetField(name, PrivateStatic);
    }

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

    // True when a UTF-16 string holds a high or low surrogate with no
    // partner: the state every encoder here replaces with U+FFFD.
    static bool HasLoneSurrogate(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return true;
            }
        }
        return false;
    }

    static int RunConnectReady()
    {
        const string crossNote = "past wait window, proceeding anyway";
        const string nativeNote = "past boot window, proceeding anyway";

        // Fresh gate state and a deterministic monotonic clock per run.
        CrossWaitStart = -1f;
        CrossProceedLoggedField.SetValue(null, false);
        NativeProceedLoggedField.SetValue(null, false);
        UnityEngine.Time.realtimeSinceStartup = 0f;

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
        UnityEngine.Time.realtimeSinceStartup = 10f;
        Check("native user id null inside boot window -> blocked early",
            !Ready(out reason) && reason.IndexOf("early", StringComparison.Ordinal) >= 0);

        // The bounded cross-user wait starts at FIRST null-id sighting.
        var cross = new SdtdConnect.Platform.PlatformManager();
        cross.User = new FakeUser();
        SdtdConnect.Platform.PlatformManager.CrossplatformPlatform = cross;
        Check("cross wait engages at first sighting",
            !Ready(out reason) && reason == "cross user not logged in yet" && CrossWaitStart == 10f);

        UnityEngine.Time.realtimeSinceStartup = 39f; // 29s elapsed, inside the window
        Check("cross wait still holds near the end of its window",
            !Ready(out reason) && reason == "cross user not logged in yet");

        // Past the window the gate proceeds so a broken EOS login cannot pin
        // the join forever - and it says so exactly once across repeated polls.
        UnityEngine.Time.realtimeSinceStartup = 41f;
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
        UnityEngine.Time.realtimeSinceStartup = 50f;
        Check("a fresh null-id episode waits again instead of proceeding instantly",
            !Ready(out reason) && reason == "cross user not logged in yet" && CrossWaitStart == 50f);

        // A platform read that throws sits inside the 10 Hz poll, so the
        // failure must be announced once, not once per poll: three polls at
        // poll rate would write three lines, and a full wait window would
        // write hundreds into the log the join harness greps.
        cross.ThrowOnUserGet = true;
        SdtdConnect.Platform.PlatformManager.NativePlatform.ThrowOnUserGet = true;
        UnityEngine.Time.realtimeSinceStartup = 51f;
        string throwLog = CaptureStderr(delegate
        {
            Ready(out reason);
            Ready(out reason);
            Ready(out reason);
        });
        Check("throwing cross-user read is announced once across polls",
            CountOccurrences(throwLog, "cross-user probe failed") == 1);
        Check("throwing native-user read is announced once across polls",
            CountOccurrences(throwLog, "native-user probe failed") == 1);
        Check("throwing platform reads name the exception type",
            CountOccurrences(throwLog, "InvalidOperationException") == 2);
        cross.ThrowOnUserGet = false;
        SdtdConnect.Platform.PlatformManager.NativePlatform.ThrowOnUserGet = false;

        return Done();
    }

    static readonly System.Reflection.FieldInfo ConnectRequestField =
        typeof(ConnectTarget).GetField("_request", PrivateStatic);

    // The production window, read from the source so a change to it is a
    // change to what this gate proves, not a number copied over here.
    static readonly float ConnectRequestWindowSec = (float)
        typeof(ConnectTarget).GetField("ConnectRequestWindowSec", PrivateStatic).GetRawConstantValue();

    static void ResetConnectRequest()
    {
        ConnectRequestField.SetValue(null, Enum.ToObject(ConnectRequestField.FieldType, 0));
    }

    // One connect attempt at a time. ConnectionManager.Connect returns before
    // the handshake, so a second request made in that gap (the auto-join
    // coroutine landing on the same menu the operator typed the F1 command
    // into, or that command typed twice) must be refused, not dialled a
    // second time. The latch has to release again for a real retry: once the
    // gate observes the live connection, once the client leaves it, and once
    // an attempt that never reported back outlives the window.
    static int RunConnectRequest()
    {
        ResetConnectRequest();
        var cm = new ConnectionManager();
        SingletonMonoBehaviour<ConnectionManager>.Instance = cm;
        GameManager.Instance = new GameManager();
        // The gate reads the connection state only past the static-data check,
        // and a client able to connect is past it.
        GameManager.Instance.bStaticDataLoaded = true;
        UnityEngine.Time.realtimeSinceStartup = 100f;
        string msg, reason;

        Check("a first request dials the server",
            ConnectTarget.TryConnect("127.0.0.1", 27025, out msg) && cm.ConnectCalls == 1);
        Check("a repeat of the same request is refused before it dials",
            !ConnectTarget.TryConnect("127.0.0.1", 27025, out msg) && cm.ConnectCalls == 1);
        Check("the refusal names the request already in flight",
            msg != null && msg.Contains("127.0.0.1:27025"));
        // A second target is a second attempt too, and racing a dialling
        // client is how a join lands on the wrong server.
        Check("a second, different request is refused as well",
            !ConnectTarget.TryConnect("10.0.0.5", 27026, out msg) && cm.ConnectCalls == 1);

        // A request that reached the server is released by the gate, the one
        // poller of the connection state, and the live session is refused for
        // its own reason.
        cm.IsConnected = true;
        Check("the gate releases a request it sees connected",
            !Ready(out reason) && reason == "already-connected");
        Check("an established session is refused by name",
            !ConnectTarget.TryConnect("127.0.0.1", 27025, out msg)
            && msg == "already connected; disconnect first"
            && cm.ConnectCalls == 1);

        // Back at the main menu: that session is over, so joining again is a
        // new join, not the repeat the latch turns away.
        cm.IsConnected = false;
        Check("a join after leaving the session is not a duplicate",
            ConnectTarget.TryConnect("127.0.0.1", 27025, out msg) && cm.ConnectCalls == 2);

        // A request whose outcome the client never saw (server down, kick
        // before the handshake) is released by the window, or the retry after
        // a failed join would be refused as a duplicate forever.
        UnityEngine.Time.realtimeSinceStartup += ConnectRequestWindowSec + 1f;
        Check("a request that never reported back expires into a retry",
            ConnectTarget.TryConnect("127.0.0.1", 27025, out msg) && cm.ConnectCalls == 3);

        ResetConnectRequest();

        // The F1 command has no connect-ready poll behind it, so the latch has
        // to release on the operator's own attempt landing too: otherwise the
        // next join is refused as a duplicate of a session already left, for
        // as long as the window lasts.
        cm.IsConnected = false;
        int f1 = cm.ConnectCalls;
        Check("an F1 attempt with no gate poller dials the server",
            ConnectTarget.TryConnect("127.0.0.1", 27025, out msg) && cm.ConnectCalls == f1 + 1);
        Check("the same F1 attempt is still refused while it dials",
            !ConnectTarget.TryConnect("127.0.0.1", 27025, out msg) && cm.ConnectCalls == f1 + 1);
        cm.IsConnected = true;
        Check("an established session is refused by name with no poller",
            !ConnectTarget.TryConnect("127.0.0.1", 27025, out msg)
            && msg == "already connected; disconnect first"
            && cm.ConnectCalls == f1 + 1);
        cm.IsConnected = false;
        Check("the next F1 join is not a duplicate of the session just left",
            ConnectTarget.TryConnect("127.0.0.1", 27025, out msg) && cm.ConnectCalls == f1 + 2);

        ResetConnectRequest();
        return Done();
    }

    // The F1 console is the only interactive UI this mod has, so it is driven
    // through the real Execute() entry point and read back from a recording
    // SdtdConsole: these are the exact lines a player sees.
    static List<string> RunF1(ConsoleCmdAbstract cmd, params string[] args)
    {
        var console = new SdtdConsole();
        SingletonMonoBehaviour<SdtdConsole>.Instance = console;
        cmd.Execute(new List<string>(args), new CommandSenderInfo());
        return console.Lines;
    }

    static bool AnyContains(IEnumerable<string> lines, string needle)
    {
        foreach (string line in lines)
            if (line != null && line.Contains(needle)) return true;
        return false;
    }

    static int RunConsole()
    {
        var connect = new ConsoleCmdConnect();
        var diag = new ConsoleCmdDiag();

        // Nothing typed: the grammar comes back instead of a bare complaint.
        List<string> lines = RunF1(connect);
        Check("connect with no argument prints the usage",
            AnyContains(lines, "connect <host> [port]"));
        Check("connect with no argument names the default port",
            AnyContains(lines, "27025"));
        Check("connect with no argument says a second line follows",
            AnyContains(lines, "once\n  the attempt ends"));
        Check("connect with no argument dials nothing",
            SingletonMonoBehaviour<ConnectionManager>.Instance == null);

        // A rejected target names the fix and echoes what the console read,
        // so a typo needs no second guess about which half was wrong.
        lines = RunF1(connect, "127.0.0.1:notaport");
        Check("a rejected port names the accepted range",
            AnyContains(lines, "port must be a number from 1 to 65535"));
        Check("a rejected port echoes the value the console read",
            AnyContains(lines, "notaport"));
        Check("a rejection does not nest one echo inside another",
            !AnyContains(lines, ") (got"));

        ResetConnectRequest();
        var cm = new ConnectionManager();
        SingletonMonoBehaviour<ConnectionManager>.Instance = cm;
        GameManager.Instance = new GameManager { bStaticDataLoaded = true };
        UnityEngine.Time.realtimeSinceStartup = 100f;

        // A token the grammar has no place for is a mistake the player made,
        // and the join still lands on the host that was typed.
        lines = RunF1(connect, "127.0.0.1", "27025", "extra");
        Check("an extra argument is announced as dropped",
            AnyContains(lines, "ignoring extra argument(s) 'extra'"));
        Check("an extra argument does not stop the join",
            cm.ConnectCalls == 1);
        Check("a dial reports the target it is connecting to",
            AnyContains(lines, "connecting to 127.0.0.1:27025"));

        // The outcome line: a request the client never saw land is over after
        // its window, and the player has to be told so. Without the report the
        // console only ever said the attempt started.
        UnityEngine.Time.realtimeSinceStartup += ConnectRequestWindowSec + 1f;
        var failed = new SdtdConsole();
        SingletonMonoBehaviour<SdtdConsole>.Instance = failed;
        ConnectTarget.ObserveConnectRequest();
        Check("a request that never landed reports the failure",
            AnyContains(failed.Lines, "connect to 127.0.0.1:27025 did not connect within"));
        Check("a failed request names the next thing to do",
            AnyContains(failed.Lines, "connect <host> [port]` again"));
        var quiet = new SdtdConsole();
        SingletonMonoBehaviour<SdtdConsole>.Instance = quiet;
        ConnectTarget.ObserveConnectRequest();
        Check("the failure is reported once, not every frame",
            quiet.Lines.Count == 0);

        // The retry after that failure must dial, not be refused as a
        // duplicate of a connect that already ended.
        lines = RunF1(connect, "127.0.0.1", "27025");
        Check("a retry after a failed join dials the server", cm.ConnectCalls == 2);
        Check("a retry after a failed join is not refused as a duplicate",
            !AnyContains(lines, "already in flight"));

        // A request that lands says so on the console, not only in the log.
        cm.IsConnected = true;
        var landed = new SdtdConsole();
        SingletonMonoBehaviour<SdtdConsole>.Instance = landed;
        ConnectTarget.ObserveConnectRequest();
        Check("a landed request is reported on the console",
            AnyContains(landed.Lines, "connected to 127.0.0.1:27025"));
        var again = new SdtdConsole();
        SingletonMonoBehaviour<SdtdConsole>.Instance = again;
        ConnectTarget.ObserveConnectRequest();
        Check("the outcome is reported once, not every frame",
            again.Lines.Count == 0);
        cm.IsConnected = false;

        // diag: every state a player can reach, and a typo that must not read
        // as a status query.
        lines = RunF1(diag, "on");
        Check("diag on enables", AnyContains(lines, "diag ON"));
        lines = RunF1(diag);
        Check("diag with no argument shows the state", AnyContains(lines, "diag ON"));
        Check("diag with no argument also shows the usage",
            AnyContains(lines, "diag [on|off|toggle|status]"));
        lines = RunF1(diag, "bogus");
        Check("an unknown diag argument names the word it read",
            AnyContains(lines, "unknown argument 'bogus'"));
        Check("an unknown diag argument lists the accepted words",
            AnyContains(lines, "on, off, toggle or status"));
        Check("an unknown diag argument still shows the state",
            AnyContains(lines, "diag ON"));
        lines = RunF1(diag, "off");
        Check("diag off disables", AnyContains(lines, "diag OFF"));
        lines = RunF1(diag, "toggle");
        Check("diag toggle flips", AnyContains(lines, "diag ON (toggled)"));
        lines = RunF1(diag, "STATUS");
        Check("diag status is case-insensitive", AnyContains(lines, "diag ON"));

        ResetConnectRequest();
        return Done();
    }

    // A stand-in for an in-flight Dns.BeginGetHostEntry. Completing it
    // signals the wait handle, exactly as the resolver does, so the
    // abandoned-lookup contract is observable without a real wedged resolver.
    sealed class FakeLookup : IAsyncResult
    {
        internal readonly ManualResetEvent Handle = new ManualResetEvent(false);
        public WaitHandle AsyncWaitHandle { get { return Handle; } }
        public object AsyncState { get { return null; } }
        public object Result { get { return null; } }
        public bool IsCompleted { get { return Handle.WaitOne(0); } }
        public bool CompletedSynchronously { get { return false; } }
        internal void Complete() { Handle.Set(); }
    }

    static readonly System.Reflection.MethodInfo WaitForLookup =
        typeof(ConnectTarget).GetMethod("WaitForLookup", PrivateStatic);

    // ConnectTarget bounds its DNS wait so a wedged resolver cannot freeze
    // the menu thread, and the abandoned lookup is still running when the
    // wait gives up. Closing the wait handle at that point disposes it out
    // from under the resolver, which then throws ObjectDisposedException
    // when it completes on its own thread. The wait must leave an
    // uncompleted operation's handle open.
    static int RunLookupWait()
    {
        // Abandoned: the wait gives up, and completing afterwards must not
        // fault on a disposed handle.
        var slow = new FakeLookup();
        bool gaveUp = !(bool)WaitForLookup.Invoke(null, new object[] { slow, 0 });
        Check("a lookup that does not finish in the window reports the timeout",
            gaveUp);
        Exception abandonThrow = null;
        var t = new System.Threading.Thread(() =>
        {
            try { slow.Complete(); }
            catch (Exception ex) { abandonThrow = ex; }
        });
        t.Start();
        t.Join();
        Check("an abandoned lookup's handle survives its later completion",
            abandonThrow == null);

        // Completed inside the window: the wait reports success and closes
        // the handle, which is the only point closing it is safe at.
        var quick = new FakeLookup();
        quick.Complete();
        bool done = (bool)WaitForLookup.Invoke(null, new object[] { quick, 5000 });
        Check("a lookup that finishes in the window reports success", done);
        Check("a completed lookup's handle is released", HandleClosed(quick));

        return Done();
    }

    static bool HandleClosed(FakeLookup lookup)
    {
        try { lookup.Handle.WaitOne(0); return false; }
        catch (ObjectDisposedException) { return true; }
    }

    static int Run()
    {
        string[] a = Environment.GetCommandLineArgs();
        string mode = a.Length > 1 ? a[1] : "";

        if (mode == "connectrequest")
        {
            return RunConnectRequest();
        }

        if (mode == "console")
        {
            return RunConsole();
        }

        if (mode == "lookupwait")
        {
            return RunLookupWait();
        }

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
            // Digits only, in every locale: a port a culture-sensitive parse
            // would take is one the shell that launched the client rejects,
            // so accepting it here joins a port the operator never set.
            CheckParse("h:+80", false, null, 0);
            CheckParse("h: 80", false, null, 0);
            CheckParse("h:٨٠", false, null, 0);
            CheckParse("h:1,234", false, null, 0);
            CheckParse("h:0x50", false, null, 0);
            CheckParse("h:0080", true, "h", 80);

            // Rejection text is the console's only feedback: it must say what
            // to type, not just that the input was rejected.
            CheckRejectionMessage("h:abc", "port", "abc", "65535");
            CheckRejectionMessage("h:65536", "port", "65535");
            CheckRejectionMessage("[::1", "[addr]");
            CheckRejectionMessage(":27025", "missing host", "connect 127.0.0.1:27025");
            CheckRejectionMessage("", "missing host", "connect 127.0.0.1:27025");
            CheckRejectionMessage("   ", "missing host");
            // A pasted port is echoed, not dumped: the reason stays readable.
            CheckRejectionMessage("h:" + new string('9', 200), "port", "...");

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

            // A host that already carries a port keeps it, so the merged
            // string must round-trip to that port and not to the argument the
            // operator typed alongside it.
            CheckMergeRoundTrip("1.2.3.4:5", "27015", "1.2.3.4", 5);
            CheckMergeRoundTrip("[::1]:9", "27015", "::1", 9);
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
            Check("null passthrough", LogText.SanitizeForLog(null) == null);
            Check("empty passthrough", LogText.SanitizeForLog("") == "");
            Check("plain text unchanged",
                LogText.SanitizeForLog("zdtd.lan:27025") == "zdtd.lan:27025");
            Check("newline flattened to space",
                LogText.SanitizeForLog("h\nFAKE") == "h FAKE");
            Check("crlf flattened to spaces",
                LogText.SanitizeForLog("h\r\nFAKE") == "h  FAKE");
            Check("tab flattened to space",
                LogText.SanitizeForLog("\t9.9.9.9") == " 9.9.9.9");
            // Invisible-format characters (Cf) are not C0 controls, so a value
            // carrying a bidi override survives char.IsControl: the terminal
            // shows nothing, but grep still matches the bytes, so a forged
            // marker reads back differently from what a harness greps for.
            Check("bidi override flattened to space",
                LogText.SanitizeForLog("g\u202Enidets") == "g nidets");
            Check("zero-width space flattened to space",
                LogText.SanitizeForLog("1.2.3.4\u200B:27025") == "1.2.3.4 :27025");
            Check("BOM flattened to space",
                LogText.SanitizeForLog("\uFEFF127.0.0.1") == " 127.0.0.1");
            Check("invisible separator flattened to space",
                LogText.SanitizeForLog("a\u2066b\u2069c") == "a b c");
            Check("accented host unchanged",
                LogText.SanitizeForLog("caf\u00E9.lan:27025") == "caf\u00E9.lan:27025");
            // The rest of general category Cf. Each block below is one a
            // category query on the Mono the game ships with does not know
            // about, so a query-based rule would pass it whole while the
            // rendered line reads as something the bytes do not spell.
            Check("soft hyphen flattened to space",
                LogText.SanitizeForLog("zd\u00ADtd.lan") == "zd td.lan");
            Check("Arabic letter mark flattened to space",
                LogText.SanitizeForLog("a\u061Cb") == "a b");
            Check("interlinear annotation flattened to space",
                LogText.SanitizeForLog("a\uFFF9b\uFFFBc") == "a b c");
            // Above the BMP: a char-at-a-time walk tests the two surrogate
            // halves of each and finds nothing, so the tag block (the
            // invisible-tag spoofing vector) and the Trojan Source Egyptian
            // format controls both reach the log intact.
            Check("tag character flattened to spaces",
                LogText.SanitizeForLog("a\U000E0061b") == "a  b");
            Check("Egyptian format control flattened to spaces",
                LogText.SanitizeForLog("a\U00013430b") == "a  b");
            // A flattened astral code point keeps its UTF-16 length, so a cap
            // counting units downstream still measures the same width.
            Check("flattened astral keeps its UTF-16 length",
                LogText.SanitizeForLog("a\U0001F600\U000E0061b").Length == 6);
            // An unpaired half is not a format character and must survive: a
            // cap must not be what turns a name into a replacement character.
            Check("lone high surrogate passes through",
                LogText.SanitizeForLog("a\ud83db") == "a\ud83db");
            // char.IsControl covers C0, DEL and C1; the Unicode line and
            // paragraph separators are the ones a log reader still lays out
            // as a line break, and the shell twin flattens the same set.
            Check("U+0085 (C1 NEL) flattened to space",
                LogText.SanitizeForLog("a\u0085result=joined") == "a result=joined");
            Check("U+2028 line separator flattened to space",
                LogText.SanitizeForLog("a\u2028result=joined") == "a result=joined");
            Check("U+2029 paragraph separator flattened to space",
                LogText.SanitizeForLog("a\u2029b") == "a b");
            Check("multi-byte text passes through unchanged",
                LogText.SanitizeForLog("zdtd.lan/\u00e9\U0001F600")
                    == "zdtd.lan/\u00e9\U0001F600");
            // One rule for every entry point: the F1 console echo runs the
            // same flattening as the -connect= warning paths, so an env-var
            // name carrying a BOM cannot reach the log unflattened, and the
            // echo itself must flatten a bidi override or an invisible
            // separator too.
            Check("LogText flattens a BOM in an env name",
                LogText.SanitizeForLog("\uFEFF7DTD_CONNECT") == " 7DTD_CONNECT");
            Check("echo flattens a bidi override",
                LogText.EchoForMessage("g\u202Enidets") == "g nidets");
            Check("echo flattens an invisible separator",
                LogText.EchoForMessage("host\u2066:27025") == "host :27025");

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

            // Opt-out shape (7DTD_CONNECT_FORCE_LOAD_SYNC): the same tokens,
            // read the other way around.
            Env(flag, "off");
            Check("VarIsOptOut true for off", EnvFlags.VarIsOptOut(flag));
            Env(flag, "1");
            Check("VarIsOptOut false for one", !EnvFlags.VarIsOptOut(flag));
            Env(flag, null);
            Check("VarIsOptOut false when unset", !EnvFlags.VarIsOptOut(flag));

            // A typo in either direction keeps the documented "unknown means
            // on" behavior but must say so: '7DTD_CONNECT_DEBUG=ture' silently
            // enabling verbose traces is exactly the silent misconfiguration
            // the warning exists for.
            Check("Parse leaves blank unset", EnvFlags.Parse("   ") == null);
            Check("Parse reads on", EnvFlags.Parse(" On ") == true);
            Check("Parse reads yes", EnvFlags.Parse("yes") == true);
            Check("Parse reads off", EnvFlags.Parse("OFF") == false);
            Check("Parse rejects unknown text", EnvFlags.Parse("ture") == null);

            Env(flag, "1");
            string cleanLog = CaptureStderr(delegate { EnvFlags.VarIsSetOn(flag); });
            Check("documented token warns nothing", cleanLog.Length == 0);
            Env(flag, "ture");
            string typoLog = CaptureStderr(delegate { EnvFlags.VarIsSetOn(flag); });
            Check("misspelled token warns with the name and value",
                typoLog.Contains(flag) && typoLog.Contains("ture"));
            Check("misspelled token still reads as on", EnvFlags.VarIsSetOn(flag));
            Check("opt-out shape warns on the same typo",
                CountOccurrences(CaptureStderr(delegate { EnvFlags.VarIsOptOut(flag); }),
                    flag) == 1);

            // A value with a newline must not forge a second log line.
            Env(flag, "ture\nFAKE-MARKER");
            string forgedLog = CaptureStderr(delegate { EnvFlags.VarIsSetOn(flag); });
            Check("warned value is flattened to one line",
                forgedLog.IndexOf("\nFAKE", StringComparison.Ordinal) < 0);

            Env(flag, null);

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
                TextUtil.CodePointCount(name) <= PlayerNames.MaxLength);
            Check("resolved name carries no outer whitespace", name == name.Trim());
            Check("resolved name carries no unpaired surrogate", !HasLoneSurrogate(name));
            Check("resolved name is NFC", name == TextUtil.NormalizeFormC(name));

            // The cap is in code points, not UTF-16 code units: an emoji name
            // is charged one character for the pair, and a name that would be
            // cut just before an astral character keeps all of it.
            const string grinning = "\U0001F600";
            string capped = PlayerNames.Cap(new string('x', PlayerNames.MaxLength) + grinning);
            Check("cap cuts a whole code point past the limit",
                capped == new string('x', PlayerNames.MaxLength));
            string emojiRun = string.Concat(grinning, grinning, grinning);
            var overlong = new StringBuilder();
            for (int k = 0; k <= PlayerNames.MaxLength; k++) overlong.Append(grinning);
            string cappedEmoji = PlayerNames.Cap(overlong.ToString());
            Check("cap counts an astral character as one, not two",
                TextUtil.CodePointCount(cappedEmoji) == PlayerNames.MaxLength);
            Check("cap never splits a surrogate pair", !HasLoneSurrogate(cappedEmoji));
            Check("cap keeps a name whose 24 characters are 48 UTF-16 units",
                cappedEmoji.Length == 2 * PlayerNames.MaxLength);

            // One name typed two ways (NFD from a macOS account, NFC from
            // elsewhere) is one identity after the cap, so the server does not
            // see two players.
            Check("NFD input is normalized to the NFC spelling",
                PlayerNames.Cap("jose\u0301") == PlayerNames.Cap("jos\u00e9")
                && PlayerNames.Cap("jose\u0301") == "jos\u00e9");

            // Cut so the limit lands between the halves of a pair.
            string edge = PlayerNames.Cap(new string('x', PlayerNames.MaxLength - 1) + grinning);
            Check("cap on a code-point boundary keeps the astral character",
                edge == new string('x', PlayerNames.MaxLength - 1) + grinning);

            // An unpaired surrogate is the state every encoder here replaces
            // with U+FFFD; a cut must not be what hands one out.
            Check("cut drops an unpaired high surrogate at the boundary",
                TextUtil.TruncateToCodePoints("ab\ud800cd", 3) == "ab");
            Check("code-point count reads an astral character as one",
                TextUtil.CodePointCount(emojiRun) == 3);

            // A code-point boundary is still inside a grapheme cluster often
            // enough to matter: the family emoji is four code points chained
            // by three ZWJs, and a cap landing on one of those joiners left a
            // name ending in a joiner that renders as a box on the server's
            // player list.
            string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466";
            Check("a cap landing on a ZWJ drops the dangling joiner",
                TextUtil.TruncateToCodePoints(family + "tail", 2) == "\U0001F468");
            Check("a cap inside the family keeps the whole family",
                TextUtil.TruncateToCodePoints(family + "tail", 7) == family);
            Check("a cap on a cluster boundary keeps every code point",
                TextUtil.CodePointCount(TextUtil.TruncateToCodePoints(family + "x", 7)) == 7);
            // A combining mark is the same case in the other alphabet: the
            // NFD spelling is normalized to NFC first, so a name that still
            // carries one is one the cap itself cut.
            Check("a cap landing on a combining mark drops it",
                TextUtil.TruncateToCodePoints("ab\u0301c", 3) == "ab");
            // The whole name is one combining mark, so the cluster walk starts
            // at the first code point and must not reach past the front of the
            // string looking for the high surrogate of a pair that is not there.
            Check("a cap over a lone combining mark yields nothing",
                TextUtil.TruncateToCodePoints("\u0301b", 1) == "");
            Check("a cap over a lone ZWJ yields nothing",
                TextUtil.TruncateToCodePoints("\u200Db", 1) == "");
            // A skin-tone modifier is a symbol, not a mark, and equally
            // renderless without its base.
            Check("a cap landing on a skin-tone modifier drops it",
                TextUtil.TruncateToCodePoints("a\U0001F3FBb", 2) == "a");
            // A flag is two regional indicators; half of one is a box.
            string flag = "\U0001F1E9\U0001F1EA";
            Check("a cap on a flag boundary keeps both halves",
                TextUtil.TruncateToCodePoints(flag + "x", 2) == flag);
            Check("a cap leaving one flag half drops it",
                TextUtil.TruncateToCodePoints("a" + flag + "b", 2) == "a");
            // The indicators run A..Z (U+1F1E6..U+1F1FF); a letter past J
            // is still half a flag.
            Check("a cap leaving half of a late-alphabet flag drops it",
                TextUtil.TruncateToCodePoints("a\U0001F1FA\U0001F1F8b", 2) == "a");
            Check("a cap that ends a run of three halves keeps two",
                TextUtil.TruncateToCodePoints(flag + "\U0001F1EBx", 3) == flag);
            // Nothing is lost off a name that was already inside the cap.
            Check("a short cluster sequence is untouched",
                TextUtil.TruncateToCodePoints(family, PlayerNames.MaxLength) == family);
            Check("PlayerNames.Cap drops a joiner left at the boundary",
                PlayerNames.Cap(new string('a', PlayerNames.MaxLength - 1) + "\u200Dx")
                    == new string('a', PlayerNames.MaxLength - 1));

            // Echo truncation shares the unit and the pair rule.
            string echo = LogText.EchoForMessage(
                new string('y', 39) + "\U0001F600 tail");
            Check("echo cut is 40 code points plus the ellipsis",
                TextUtil.CodePointCount(echo) == 43 && !HasLoneSurrogate(echo));
            Check("echo of a short astral value is untouched",
                LogText.EchoForMessage(grinning) == grinning);

            // The name reaches the server, so Normalize must strip the
            // characters that forge a line in a server log, and a length cap
            // must not cut a surrogate pair in half.
            Check("Normalize flattens a newline in the name",
                PlayerNames.Normalize("al\nice: admin") == "al ice: admin");
            Check("Normalize drops a bidi override",
                PlayerNames.Normalize("g\u202Enidets") == "g nidets");
            Check("Normalize caps an overlong name",
                PlayerNames.Normalize(new string('a', 40)).Length == PlayerNames.MaxLength);
            Check("Normalize keeps an accented name",
                PlayerNames.Normalize("ren\u00E9") == "ren\u00E9");
            // A name capped at MaxLength with an astral character landing on
            // the boundary must not end in an unpaired surrogate.
            string cappedName = PlayerNames.Normalize(new string('a', PlayerNames.MaxLength - 1) + "\U0001F600");
            Check("Normalize never ends on a lone high surrogate",
                cappedName.Length > 0 && !char.IsHighSurrogate(cappedName[cappedName.Length - 1]));
            Check("Normalize returns null for null", PlayerNames.Normalize(null) == null);
            Check("Normalize returns null for empty", PlayerNames.Normalize("") == null);
            Check("Normalize trims whitespace exposed by the length cap",
                PlayerNames.Normalize(new string('a', PlayerNames.MaxLength - 1) + " tail")
                    == new string('a', PlayerNames.MaxLength - 1));
            // A value that is nothing but stripped characters normalizes to
            // nothing, which is the signal the caller falls back on.
            Check("Normalize empties a value of only invisible characters",
                string.IsNullOrEmpty(PlayerNames.Normalize("\u202E\u200B")));

            // A name that an OS can hold (astral characters in a user or
            // machine name) must not be cut mid-surrogate: the server stores
            // and echoes whatever the client sends. The cap counts code
            // points, so the astral character is the 24th and the name is
            // legal; the rule under test is that it is not cut in half.
            string astral = PlayerNames.Normalize(
                "abcdefghijklmnopqrstuvw" + char.ConvertFromUtf32(0x1F600));
            Check("capped astral name fits the cap",
                TextUtil.CodePointCount(astral) <= PlayerNames.MaxLength);
            // One character over, the astral character is the 25th and drops
            // whole rather than splitting.
            Check("an astral name one past the cap loses the whole character",
                PlayerNames.Cap(
                    "abcdefghijklmnopqrstuvwx" + char.ConvertFromUtf32(0x1F600))
                    == "abcdefghijklmnopqrstuvwx");
            Check("capped astral name keeps whole characters",
                !HasLoneSurrogate(astral));
            Check("an overlong astral name is cut on a code-point boundary",
                !HasLoneSurrogate(PlayerNames.Normalize(
                    new string('a', PlayerNames.MaxLength) + char.ConvertFromUtf32(0x1F600))));
            Check("capped name below the cap is untouched",
                PlayerNames.Normalize("short") == "short");

            return Done();
        }

        if (mode == "probefailure")
        {
            // The announce-once channel. Silence is indistinguishable from a
            // healthy quiet join, but a 10 Hz heartbeat that keeps throwing
            // would bury the markers join harnesses grep for. Two properties
            // matter and neither is visible in the source: the latch fires
            // once per probe name, and it is keyed per name so a dead probe
            // cannot mute another (a shared latch buried the synthetic-id
            // notice, the one failure that silently changes the server-side
            // player identity).
            const string prefix = "pf";

            string first = CaptureStderr(delegate
            {
                ProbeFailure.Once(prefix + "-boom", new InvalidOperationException("boot gate dead"));
            });
            Check("first exception announces", first.Contains(prefix + "-boom"));
            Check("announcement carries the exception text", first.Contains("boot gate dead"));
            Check("announcement uses the mod log prefix", first.Contains("[7dtd-fastconnect]"));
            Check("announcement says later failures are muted",
                first.Contains("further failures muted"));
            Check("first announcement is a single line",
                CountOccurrences(first, prefix + "-boom") == 1);

            // Same probe, different failure: the latch is the point.
            string repeat = CaptureStderr(delegate
            {
                ProbeFailure.Once(prefix + "-boom", new InvalidOperationException("spawn gate dead"));
                ProbeFailure.Once(prefix + "-boom", new InvalidOperationException("load gate dead"));
            });
            Check("repeat failures for the same probe stay silent", repeat == "");

            // A different probe must still be heard.
            string other = CaptureStderr(delegate
            {
                ProbeFailure.Once(prefix + "-synthetic-id", new InvalidOperationException("no id"));
            });
            Check("a different probe still announces", other.Contains(prefix + "-synthetic-id"));
            Check("a different probe carries its own detail", other.Contains("no id"));

            // A null exception is not a failure: it may not log, and it may
            // not burn the latch for the real one.
            Check("null exception is silent", CaptureStderr(delegate
            {
                ProbeFailure.Once(prefix + "-null", (Exception)null);
            }) == "");
            string afterNull = CaptureStderr(delegate
            {
                ProbeFailure.Once(prefix + "-null", new InvalidOperationException("the real failure"));
            });
            Check("a null exception does not consume the latch", afterNull.Contains("the real failure"));

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

        if (mode == "fuzz-text")
        {
            return RunFuzzText();
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

    // Mirrors ConnectTarget.IsInvisibleFormat: the Cf characters char.IsControl
    // does not cover, which a terminal renders as nothing.
    static bool IsInvisibleFormat(char c)
    {
        return (c >= '\u200B' && c <= '\u200F')
            || (c >= '\u2060' && c <= '\u2064')
            || (c >= '\u2066' && c <= '\u2069')
            || (c >= '\u202A' && c <= '\u202E')
            || c == '\uFEFF';
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

    // Non-ASCII spliced into the grammar: accented and CJK letters, an NFD
    // pair, an astral character, a replacement character, the two Unicode
    // line separators, a C1 control, and an unpaired surrogate. Every one of
    // these has to survive parse, merge, and log flattening unchanged in
    // shape, which an ASCII-only alphabet never exercises.
    static readonly string[] FuzzUnicode =
    {
        "\u00e9", "e\u0301", "\u4e2d\u6587", "\U0001F600", "\uFFFD",
        "\u2028", "\u2029", "\u0085", "\u00ad", "\ud83d", "\ude00"
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
        // Invisible-format characters: invisible to a reader, present to grep.
        if (r.Next(6) == 0) raw = "\u202E" + raw + "\u202C";
        if (r.Next(6) == 0) raw = raw.Replace(":", "\u200B:");
        if (r.Next(8) == 0) raw = "\uFEFF" + raw;
        // A random splice of the FuzzUnicode set: line separators, a C1
        // control, an NFD pair and an astral character all have to survive
        // parse, merge and log flattening unchanged in shape.
        if (r.Next(3) == 0)
        {
            var mixed = new StringBuilder();
            int n = r.Next(1, 5);
            for (int k = 0; k < n; k++) mixed.Append(FuzzUnicode[r.Next(FuzzUnicode.Length)]);
            raw = mixed.ToString() + raw;
        }
        return raw;
    }

    static void FuzzOne(string raw, int i)
    {
        string label = "raw='" + EscapeForLog(raw) + "'";

        // Totality: none of the entry points may throw on any input.
        string san;
        try { san = LogText.SanitizeForLog(raw); }
        catch (Exception ex) { CheckFuzz(label + " SanitizeForLog threw", false); Console.WriteLine("     " + ex.GetType().Name); return; }

        if (san != null)
        {
            CheckFuzz(label + " sanitize preserves length", san.Length == (raw ?? "").Length);
            bool clean = true;
            foreach (char c in san)
            {
                if (char.IsControl(c) || c == '\u2028' || c == '\u2029' || IsInvisibleFormat(c))
                {
                    clean = false;
                    break;
                }
            }
            CheckFuzz(label + " sanitize strips control and line-breaking chars", clean);
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
        // SetEnvironmentVariable re-encodes the value in the platform code
        // page, so an input carrying an unpaired surrogate is refused by the
        // runtime itself; the parse, merge, and sanitize invariants above
        // already ran on it.
        if ((i % 64) == 0 && !HasLoneSurrogate(raw))
        {
            try
            {
                Environment.SetEnvironmentVariable(ConnectTarget.EnvVar, raw);
                string srcHost; int srcPort; string srcSource;
                bool srcOk = ConnectTarget.TryFromLaunchContext(out srcHost, out srcPort, out srcSource);
                if (srcOk)
                {
                    CheckFuzz(label + " env source single-line",
                        srcSource.IndexOf('\n') < 0 && srcSource.IndexOf('\r') < 0
                        && srcSource.IndexOf('\u2028') < 0 && srcSource.IndexOf('\u2029') < 0);
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

    // ------------------------------------------------------------------
    // Fuzz target for the identity path (TextUtil / PlayerNames /
    // LogText.EchoForMessage / EnvFlags), the mod's second untrusted-input
    // surface. A display name arrives from the 7DTD_FASTCONNECT_NAME
    // override, a steam://run URL or an OS account name, and is stored,
    // normalized, length-capped and sent to the server, where it lands in
    // the server log and the player list. Everything here counts code
    // points rather than UTF-16 units and cuts on a pair boundary, which is
    // where a truncation bug turns into a name the player did not type, so
    // each generated value is checked against invariants that must hold for
    // ALL inputs, not against a fixed table. The generator is built from
    // UTF-16 unit pieces so it can emit an unpaired surrogate, which a
    // char-based generator cannot.
    // ------------------------------------------------------------------
    const int FuzzTextSeed = 20260928;
    static int _fuzzTextReported;

    // State the generator has to reach, so a lane that stopped producing
    // interesting inputs fails instead of reporting a clean run over nothing.
    static int _fuzzTextCut;     // a cap that actually shortened a value
    static int _fuzzTextPair;    // a value carrying an astral character
    static int _fuzzTextAway;    // a name that normalized away to nothing
    static int _fuzzTextNfc;     // a value NFC rewrote (an NFD run)

    static void CheckFuzzText(string name, bool cond)
    {
        if (cond) return;
        _fails++;
        if (_fuzzTextReported < 20)
        {
            _fuzzTextReported++;
            Console.WriteLine("FAIL " + name);
        }
    }

    // Code-point count computed by walking the string a different way from
    // TextUtil (skip a low surrogate that follows a high one, rather than
    // advancing the index inside the loop). Agreement is the assertion, so a
    // defect in one counting rule cannot hide in the other.
    static int OracleCodePointCount(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        int count = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsLowSurrogate(value[i]) && i > 0 && char.IsHighSurrogate(value[i - 1]))
                continue;
            count++;
        }
        return count;
    }

    static bool EndsOnHighSurrogate(string value)
    {
        return value != null && value.Length > 0 && char.IsHighSurrogate(value[value.Length - 1]);
    }

    static bool IsPrefixOf(string prefix, string whole)
    {
        return prefix != null && whole != null
            && prefix.Length <= whole.Length
            && string.CompareOrdinal(whole, 0, prefix, 0, prefix.Length) == 0;
    }

    static bool IsLogSafeText(string value)
    {
        if (value == null) return true;
        foreach (char c in value)
        {
            if (char.IsControl(c) || c == '\u2028' || c == '\u2029' || IsInvisibleFormat(c))
                return false;
        }
        return true;
    }

    // UTF-16 unit pieces. An unpaired surrogate, a valid pair, a combining
    // mark whose base makes NFC shorter, the characters LogText flattens, and
    // ordinary safe text.
    static readonly string[] FuzzTextUnits =
    {
        "a", "Z", "7", "_", " ", "\t", "\n", "\0", "\u00e9", "\u4e2d",
        "\U0001F600", "\u0301", "e\u0301", "\ud83d", "\ude00", "\ud83d\ude00",
        "\u2028", "\u2029", "\u202E", "\uFEFF", "\u0085", "\u00ad", "\uFFFD",
        "\ud800", "\udfff"
    };

    static string GenText(Random r, int i)
    {
        switch (i % 8)
        {
            case 0:
            {
                var sb = new StringBuilder();
                int n = r.Next(0, 24);
                for (int k = 0; k < n; k++) sb.Append(FuzzTextUnits[r.Next(FuzzTextUnits.Length)]);
                return sb.ToString();
            }
            case 1:
            {
                // Safe ASCII only: the shape that must pass through untouched.
                var sb = new StringBuilder();
                int n = r.Next(0, 40);
                for (int k = 0; k < n; k++) sb.Append((char)('a' + r.Next(26)));
                return sb.ToString();
            }
            case 2:
            {
                // Exactly the cap in code points, ending on an astral
                // character: the boundary the cut has to land on without
                // splitting the pair.
                var sb = new StringBuilder();
                while (OracleCodePointCount(sb.ToString()) < PlayerNames.MaxLength - 1)
                    sb.Append((char)('a' + r.Next(26)));
                sb.Append("\ud83d\ude00");
                return sb.ToString();
            }
            case 3:
            {
                // One code point short of the cap, then a pair: the cap must
                // drop the whole character, not half of it.
                var sb = new StringBuilder();
                while (OracleCodePointCount(sb.ToString()) < PlayerNames.MaxLength - 1)
                    sb.Append((char)('a' + r.Next(26)));
                sb.Append('a');
                sb.Append("\ud83d");
                sb.Append("\ude00");
                sb.Append((char)('a' + r.Next(26)));
                return sb.ToString();
            }
            case 4:
            {
                // A lone surrogate with real text on both sides, so a cap
                // that cuts inside the pair leaves a lone one behind.
                var sb = new StringBuilder();
                int n = r.Next(0, 20);
                for (int k = 0; k < n; k++) sb.Append((char)('a' + r.Next(26)));
                sb.Append(r.Next(2) == 0 ? "\ud83d" : "\ude00");
                int m = r.Next(0, 20);
                for (int k = 0; k < m; k++) sb.Append((char)('a' + r.Next(26)));
                return sb.ToString();
            }
            case 5:
            {
                // A long combining-mark run: NFC composes the whole run into
                // one code point, so the cap is reached far later in code
                // points than in characters.
                var sb = new StringBuilder();
                int n = r.Next(1, 40);
                for (int k = 0; k < n; k++) { sb.Append('e'); sb.Append('\u0301'); }
                return sb.ToString();
            }
            case 6:
            {
                // Padding and invisible characters around a name: Normalize
                // trims, and the invisible ones are flattened rather than
                // kept, so the stored identity differs from the input.
                return "  \u200b\t\u202e" + GenText(r, 1) + "\u00ad  ";
            }
            default:
            {
                // Nothing but characters the log rule flattens: the name
                // normalizes away to empty, and the caller must see that as
                // the same signal as an absent name.
                var sb = new StringBuilder();
                int n = r.Next(1, 12);
                for (int k = 0; k < n; k++)
                {
                    int pick = r.Next(4);
                    if (pick == 0) sb.Append('\u200B');
                    else if (pick == 1) sb.Append('\uFEFF');
                    else if (pick == 2) sb.Append('\u202E');
                    else sb.Append('\t');
                }
                return sb.ToString();
            }
        }
    }

    static void FuzzOneText(string raw, int i)
    {
        string label = "text='" + EscapeForLog(raw) + "'";
        int rawPoints = OracleCodePointCount(raw);

        // TextUtil.CodePointCount: the unit every cap in the mod is counted
        // in. It must agree with the independent walk above, never exceed the
        // UTF-16 length, and be zero exactly for an empty value.
        int count;
        try { count = TextUtil.CodePointCount(raw); }
        catch (Exception ex) { CheckFuzzText(label + " CodePointCount threw", false); Console.WriteLine("     " + ex.GetType().Name); return; }
        CheckFuzzText(label + " code-point count matches the oracle", count == rawPoints);
        CheckFuzzText(label + " code-point count within the UTF-16 length", count <= (raw == null ? 0 : raw.Length));
        CheckFuzzText(label + " code-point count zero only when empty", (count == 0) == string.IsNullOrEmpty(raw));

        // TextUtil.NormalizeFormC: NFC, so two spellings of one name are one
        // identity. It must be total (an unpaired surrogate is a value .NET
        // refuses to normalize), idempotent, and never longer in code points
        // than the input it replaces.
        string nfc;
        try { nfc = TextUtil.NormalizeFormC(raw); }
        catch (Exception ex) { CheckFuzzText(label + " NormalizeFormC threw", false); Console.WriteLine("     " + ex.GetType().Name); return; }
        CheckFuzzText(label + " NFC is a fixed point",
            nfc == null || TextUtil.NormalizeFormC(nfc) == nfc);
        CheckFuzzText(label + " NFC never grows the code-point count",
            nfc == null || TextUtil.CodePointCount(nfc) <= rawPoints);
        if (nfc != null && nfc != raw) _fuzzTextNfc++;
        CheckFuzzText(label + " NFC preserves null/empty", (nfc == null) == (raw == null) && (nfc == "") == (raw == ""));

        // TextUtil.TruncateToCodePoints across the boundary cases: 0, one
        // under the cap, exactly the cap, over the cap, and one over the
        // input's own length. Every one must stay within n code points, stay
        // a prefix of the input, and never end on half a pair.
        int[] limits =
        {
            0, 1, PlayerNames.MaxLength - 1, PlayerNames.MaxLength,
            PlayerNames.MaxLength + 1, rawPoints, rawPoints + 2
        };
        for (int li = 0; li < limits.Length; li++)
        {
            int n = limits[li];
            string cut;
            try { cut = TextUtil.TruncateToCodePoints(raw, n); }
            catch (Exception ex)
            {
                CheckFuzzText(label + " TruncateToCodePoints threw", false);
                Console.WriteLine("     " + ex.GetType().Name);
                continue;
            }
            string at = label + " truncate(" + n + ")";
            CheckFuzzText(at + " stays within the cap",
                TextUtil.CodePointCount(cut) <= n || n < 0);
            CheckFuzzText(at + " is a prefix of the input", IsPrefixOf(cut, raw));
            // A cut never ends on half a pair. A value that already carried
            // an unpaired high surrogate comes back untouched, so the cap is
            // not what put it there.
            CheckFuzzText(at + " does not split a surrogate pair",
                !EndsOnHighSurrogate(cut) || cut == raw);
            // A cap of at least the input's own length is the identity: the
            // caller must get its own string back, not a rewritten one.
            if (n >= rawPoints)
                CheckFuzzText(at + " leaves a short value untouched", cut == raw);
            else if (cut.Length != raw.Length)
                _fuzzTextCut++;
            // Truncating an already-truncated value changes nothing, so a
            // name capped twice (fallback over override) is the one name.
            try
            {
                CheckFuzzText(at + " is a fixed point",
                    TextUtil.TruncateToCodePoints(cut, n) == cut);
            }
            catch (Exception ex)
            {
                CheckFuzzText(at + " re-truncate threw", false);
                Console.WriteLine("     " + ex.GetType().Name);
            }
        }

        // PlayerNames.Cap: the stored identity. Within the stock cap, in one
        // normalization form, and a fixed point, so the override and the
        // fallback can never disagree.
        string capped;
        try { capped = PlayerNames.Cap(raw); }
        catch (Exception ex) { CheckFuzzText(label + " Cap threw", false); Console.WriteLine("     " + ex.GetType().Name); return; }
        CheckFuzzText(label + " cap is null only for a null name", (capped == null) == (raw == null));
        CheckFuzzText(label + " cap fits the stock limit",
            capped == null || TextUtil.CodePointCount(capped) <= PlayerNames.MaxLength);
        // The cap cuts what it was handed, so a cut (any output that is not
        // the normalized value itself) never ends on half a pair. A value
        // that already carried an unpaired high surrogate comes back whole,
        // which is the input's state, not one the cap added.
        string capInput = TextUtil.NormalizeFormC(raw);
        CheckFuzzText(label + " a capped name is not left half a pair",
            capped == capInput || !EndsOnHighSurrogate(capped));
        if (capped != null)
        {
            CheckFuzzText(label + " cap is a fixed point", PlayerNames.Cap(capped) == capped);
            CheckFuzzText(label + " cap is already NFC", TextUtil.NormalizeFormC(capped) == capped);
        }

        // PlayerNames.Normalize: what a caller actually stores. Flattened,
        // trimmed, capped, and null for an absent value.
        string name;
        try { name = PlayerNames.Normalize(raw); }
        catch (Exception ex) { CheckFuzzText(label + " Normalize threw", false); Console.WriteLine("     " + ex.GetType().Name); return; }
        if (string.IsNullOrEmpty(raw))
        {
            CheckFuzzText(label + " absent name normalizes to null", name == null);
        }
        else
        {
            CheckFuzzText(label + " name fits the stock limit",
                name == null || TextUtil.CodePointCount(name) <= PlayerNames.MaxLength);
            CheckFuzzText(label + " name carries nothing the log rule flattens", IsLogSafeText(name));
            CheckFuzzText(label + " name is trimmed", name == null || name == name.Trim());
            CheckFuzzText(label + " name is capped and normalized already",
                name == null || PlayerNames.Cap(name) == name);
            // A name of nothing but flattened characters normalizes away;
            // Resolve falls back for it, and the caller sees null so the
            // fallback runs.
            CheckFuzzText(label + " a name that flattens away is null",
                (name == null) == (LogText.SanitizeForLog(raw).Trim().Length == 0));
            if (name == null) _fuzzTextAway++;
            if (raw.IndexOf('\uD83D') >= 0 || raw.IndexOf('\uD800') >= 0) _fuzzTextPair++;
        }

        // LogText.EchoForMessage: the one-line echo the F1 console and the
        // rejection messages show. Safe text short enough to fit comes back
        // trimmed; a cut one never leaves half a pair or an unflat
        // character.
        string echo;
        try { echo = LogText.EchoForMessage(raw); }
        catch (Exception ex) { CheckFuzzText(label + " EchoForMessage threw", false); Console.WriteLine("     " + ex.GetType().Name); return; }
        CheckFuzzText(label + " echo carries nothing the log rule flattens", IsLogSafeText(echo));
        // Same rule on the echo: a cut (anything but the flattened, trimmed
        // value itself) never ends on half a pair.
        string echoInput = LogText.SanitizeForLog(raw).Trim();
        CheckFuzzText(label + " a cut echo is not left half a pair",
            echo == echoInput || !EndsOnHighSurrogate(echo));
        if (raw != null && IsLogSafeText(raw) && rawPoints <= 40)
            CheckFuzzText(label + " a short safe echo is the trimmed value",
                echo == LogText.SanitizeForLog(raw).Trim());

        // EnvFlags: the boolean knobs, read as text. A knob is on exactly
        // when it is non-blank and not an opt-out token, the documented
        // tokens resolve the documented way in any case and with padding,
        // an undocumented one reads as on, and nothing throws. The table is
        // indexed off the iteration so every entry is exercised.
        bool optOut, setOn, known;
        try
        {
            optOut = EnvFlags.IsOptOut(raw);
            setOn = EnvFlags.IsSetOn(raw);
            known = string.IsNullOrWhiteSpace(raw) || EnvFlags.Parse(raw) != null;
        }
        catch (Exception ex) { CheckFuzzText(label + " EnvFlags threw", false); Console.WriteLine("     " + ex.GetType().Name); return; }
        CheckFuzzText(label + " a knob is on exactly when it is non-blank and not an opt-out",
            setOn == (!string.IsNullOrWhiteSpace(raw) && !optOut));
        CheckFuzzText(label + " a blank knob is neither on nor off",
            string.IsNullOrWhiteSpace(raw) == (!setOn && !optOut));
        CheckFuzzText(label + " a blank knob is a known value",
            !string.IsNullOrWhiteSpace(raw) || known);
        int slot = Math.Abs(i) % FuzzBoolTokens.Length;
        string tok = FuzzBoolTokens[slot];
        CheckFuzzText(label + " the token table resolves as documented",
            EnvFlags.IsOptOut(tok) == FuzzBoolTokensOptOut[slot]
            && EnvFlags.IsSetOn(tok) == !FuzzBoolTokensOptOut[slot]
            && (EnvFlags.Parse(tok) != null) == FuzzBoolTokensKnown[slot]);
    }

    // The documented boolean vocabulary in both directions and in any case,
    // then the undocumented values the warning exists for: they read as on,
    // and Parse returns no verdict for them. FuzzBoolTokensOptOut and
    // FuzzBoolTokensKnown carry the documented verdict for each entry.
    static readonly string[] FuzzBoolTokens =
    {
        "0", "false", "FALSE", "no", "No", "off", "OFF", " off ", "\t0\t",
        "1", "true", "TRUE", "yes", "YES", "on", "On", " on ", "\tyes\t",
        "2", "maybe", "-1", "00", "o", "yess", "2 "
    };

    static readonly bool[] FuzzBoolTokensOptOut =
    {
        true, true, true, true, true, true, true, true, true,
        false, false, false, false, false, false, false, false, false,
        false, false, false, false, false, false, false
    };

    static readonly bool[] FuzzBoolTokensKnown =
    {
        true, true, true, true, true, true, true, true, true,
        true, true, true, true, true, true, true, true, true,
        false, false, false, false, false, false, false
    };

    static int RunFuzzText()
    {
        const int iterations = 24000;
        var rng = new Random(FuzzTextSeed);
        int before = _fails;
        for (int i = 0; i < iterations; i++)
            FuzzOneText(GenText(rng, i), i);
        int found = _fails - before;
        Console.WriteLine("fuzz-text: seed=" + FuzzTextSeed + " iterations=" + iterations
            + " cuts=" + _fuzzTextCut + " astral=" + _fuzzTextPair
            + " normalized_away=" + _fuzzTextAway + " nfc_rewrites=" + _fuzzTextNfc
            + " violations=" + found);
        // A generator that stopped reaching a state would make the run
        // vacuous without any invariant failing, so the states the lane
        // exists to cover are asserted as reached.
        CheckFuzzText("fuzz-text reached a cap that shortened a value", _fuzzTextCut > 0);
        CheckFuzzText("fuzz-text reached an astral character", _fuzzTextPair > 0);
        CheckFuzzText("fuzz-text reached a name that normalized away", _fuzzTextAway > 0);
        CheckFuzzText("fuzz-text reached a value NFC rewrites", _fuzzTextNfc > 0);
        return Done();
    }

    static void Env(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value); // null subs
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
