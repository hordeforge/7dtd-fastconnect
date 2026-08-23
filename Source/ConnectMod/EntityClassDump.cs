using System;
using HarmonyLib;

namespace SdtdConnect
{
    /// <summary>
    /// Optional RE dump of EntityClass ids (env 7DTD_DUMP_ENTITY_CLASS=1).
    /// Stock keys list[name.GetHashCode()] = class; capture real hashes for zdtd ECD.
    /// </summary>
    static class EntityClassDump
    {
        static int _logged;

        // Env cannot change mid-process; EntityClass.Add fires for every
        // entityclass at load, so read the flag once instead of per add.
        static readonly bool _dumpEnabled = EnvFlags.VarIsSetOn("7DTD_DUMP_ENTITY_CLASS");

        [HarmonyPatch(typeof(EntityClass), nameof(EntityClass.Add))]
        static class Patch_Add
        {
            static void Postfix(string _entityClassname, EntityClass _entityClass)
            {
                if (!_dumpEnabled) return;
                if (_entityClassname == null) return;
                // Log players + first zombies + any name containing zombie/animal/trader
                bool interesting =
                    _entityClassname.StartsWith("player", StringComparison.OrdinalIgnoreCase)
                    || _entityClassname.IndexOf("zombie", StringComparison.OrdinalIgnoreCase) >= 0
                    || _entityClassname.IndexOf("animal", StringComparison.OrdinalIgnoreCase) >= 0
                    || _entityClassname.IndexOf("trader", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!interesting) return;
                if (_logged > 80) return;
                int id = _entityClassname.GetHashCode();
                // Prefer list key if GetId works
                try
                {
                    int gid = EntityClass.GetId(_entityClassname);
                    if (gid != -1) id = gid;
                }
                catch { /* ignore */ }
                Log.Out("[7dtd-connect] EntityClass.Add name=" + _entityClassname + " id=" + id);
                _logged++;
            }
        }
    }
}
