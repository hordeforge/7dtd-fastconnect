using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SdtdConnect
{
    /// <summary>
    /// The Local-host world load stops inside stock code between StartAsServer's
    /// second post-createWorld "yield return null" and "Created player with id=".
    /// The flattened-iterator trace proved Unity keeps resuming the coroutine, so
    /// the main thread is blocked synchronously somewhere in that stretch. Log
    /// entry and exit of every call it makes: the one with an enter and no leave
    /// is the culprit.
    /// </summary>
    static class LocalHostSpawnTrace
    {
        static readonly (string Type, string Method)[] Targets =
        {
            ("PlayerProfile", "LoadLocalProfile"),
            ("PlayerDataFile", "Load"),
            ("SpawnPointList", "GetRandomSpawnPosition"),
            ("DynamicPrefabDecorator", "GetClosestPOIToWorldPos"),
            ("EntityFactory", "CreateEntity"),
            ("EntityFactory+CreateEntityOperation", "Start"),
            ("EntityFactory+CreateEntityOperation", "LoadAssets"),
            ("EntityFactory+CreateEntityOperation", "WaitForLoadingComplete"),
            ("EntityFactory+CreateEntityOperation", "CompleteEntity"),
            ("EntityInstanceAssets", "Load"),
            ("EntityInstanceAssets", "WaitForComplete"),
            ("EModelInstanceAssets", "Load"),
            ("EModelInstanceAssets", "WaitForComplete"),
            ("EntityFactory", "addEntityComponent"),
            ("EntityPlayerLocal", "Init"),
            ("EntityPlayer", "Init"),
            ("EntityAlive", "Init"),
            ("Entity", "Init"),
            ("EntityAlive", "InitStats"),
            ("EntityAlive", "switchModelView"),
            ("EntityAlive", "InitPostCommon"),
            ("EntityAlive", "AddCharacterController"),
            ("EntityPlayer", "InitStats"),
            ("EntityPlayerLocal", "InitStats"),
            ("EntityPlayerLocal", "switchModelView"),
            ("EntityPlayerLocal", "AddCharacterController"),
            ("EntityBuffs", "AddBuff"),
            ("World", "SpawnEntityInWorld"),
            ("IMapChunkDatabase", "TryCreateOrLoad"),
        };

        internal static void Apply(Harmony harmony)
        {
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(LocalHostSpawnTrace), nameof(Enter)));
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(LocalHostSpawnTrace), nameof(Leave)));
            int ok = 0, fail = 0;
            foreach ((string typeName, string methodName) in Targets)
            {
                Type type = AccessTools.TypeByName(typeName);
                if (type == null) { fail++; continue; }
                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
                {
                    if (method.Name != methodName || method.IsAbstract || method.ContainsGenericParameters) continue;
                    try { harmony.Patch(method, prefix, postfix); ok++; }
                    catch { fail++; }
                }
            }
            Log.Out("[7dtd-connect] Local-host spawn trace patches ok=" + ok + " fail=" + fail);
        }

        static void Enter(MethodBase __originalMethod)
            => Log.Out("[7dtd-connect] spawn trace: enter " + Name(__originalMethod));

        static void Leave(MethodBase __originalMethod)
            => Log.Out("[7dtd-connect] spawn trace: leave " + Name(__originalMethod));

        static string Name(MethodBase method)
            => (method?.DeclaringType?.Name ?? "?") + "." + (method?.Name ?? "?");
    }
}
