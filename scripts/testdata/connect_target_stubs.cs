// Compiler-only stand-ins for the 7 Days To Die game API, used by
// scripts/test_connect_target_parse.sh so REAL production sources can be
// compiled and their offline-testable paths executed headlessly without a
// game install (ConnectTarget parsing/launch context, ConnectReady gate,
// PlayerNames, AutomationMode, BootUnblock's force-load-sync contract).
//
// These types are NOT shipped and NOT referenced by the mod build. Every
// member that the tested paths must never reach throws
// NotImplementedException: if a future edit makes an exercised path call into
// a stub, the tests fail loudly instead of passing against fake behavior.
using System;

namespace UnityEngine
{
    public static class Time
    {
        public static float unscaledTime;
    }

    // Read/written only by BootUnblock.ApplyFrameUncap; plain state so the
    // frame-uncap path stays compilable (its behavior is not asserted here).
    // ThreadPriority mirrors the game's UnityEngine.ThreadPriority.
    public enum ThreadPriority { Low, BelowNormal, Normal, AboveNormal, High }

    public static class Application
    {
        public static bool runInBackground;
        public static int targetFrameRate;
        public static ThreadPriority backgroundLoadingPriority;
    }

    public static class QualitySettings
    {
        public static int vSyncCount;
    }
}

namespace HarmonyLib
{
    // Attribute-shaped enough for compilation: patch classes are never
    // processed in these tests.
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class HarmonyPatchAttribute : Attribute
    {
        public HarmonyPatchAttribute(Type target, string methodName) { }
    }
}

namespace SdtdConnect
{
    public static class Log
    {
        public static void Out(string m) { Console.Error.WriteLine(m); }
        public static void Warning(string m) { Console.Error.WriteLine(m); }
        public static void Error(string m) { Console.Error.WriteLine(m); }
    }

    public class SingletonMonoBehaviour<T> where T : class
    {
        public static T Instance;
    }

    public enum GameInfoString { IP, GameType, GameName, GameHost, LevelName, GameMode, ServerVersion }
    public enum GameInfoInt { Port, WorldSize, CurrentPlayers, MaxPlayers, FreePlayerSlots }
    public enum GameInfoBool { IsDedicated, EACEnabled, IsPasswordProtected }

    public class GameServerInfo
    {
        public void SetValue(GameInfoString k, string v) { throw new NotImplementedException(); }
        public void SetValue(GameInfoInt k, int v) { throw new NotImplementedException(); }
        public void SetValue(GameInfoBool k, bool v) { throw new NotImplementedException(); }
    }

    public class ConnectionManager
    {
        // Plain state, not a throwing property: ConnectReady's gate reads it
        // on every poll. The connect actions below still throw, so no test
        // can accidentally "join" against stubs.
        public bool IsConnected;
        public GameServerInfo LastGameServerInfo { set { throw new NotImplementedException(); } }
        public void Connect(GameServerInfo gsi) { throw new NotImplementedException(); }
    }

    public class GameManager
    {
        public static GameManager Instance;
        public bool showOpenerMovieOnLoad;
        public bool bStaticDataLoaded;
    }

    public class VersionInformation { public string SerializableString; }
    public static class Constants { public static VersionInformation cVersionInformation; }

    // Real static field: BootUnblock.ApplyForceLoadSync flips it via
    // reflection, and the forcesync tests assert on the flip.
    public static class LoadManager { public static bool forceLoadSync; }

    public enum EnumGamePrefs { SkipSpawnButton }
    public static class GamePrefs
    {
        public static void Set(EnumGamePrefs k, object v) { throw new NotImplementedException(); }
    }

    namespace Platform
    {
        public abstract class IUser { public object PlatformUserId; }
        public class PlatformManager
        {
            public static PlatformManager NativePlatform;
            public static PlatformManager CrossplatformPlatform;
            public IUser User;
        }
    }

    public static class PermissionsManager
    {
        // Delegate so ConnectReady tests must configure it explicitly; the
        // default keeps the never-reach-me contract (throws).
        public static Func<bool> IsMultiplayerAllowed =
            () => throw new NotImplementedException();
    }
}
