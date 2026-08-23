// Compiler-only stand-ins for the 7 Days To Die game API, used by
// scripts/test_connect_target_parse.sh so the REAL production
// Source/ConnectMod/ConnectTarget.cs can be compiled and its pure parsing
// paths (ConnectTarget.TryParse / ConnectTarget.MergePortArg /
// ConnectTarget.TryFromLaunchContext) executed headlessly without a game
// install.
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

    public static class AutomationMode
    {
        public static bool Enabled;
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
        public bool IsConnected { get { throw new NotImplementedException(); } }
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
        public static bool IsMultiplayerAllowed() { throw new NotImplementedException(); }
    }
}
