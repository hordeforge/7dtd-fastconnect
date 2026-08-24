using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace SdtdConnect
{
    /// <summary>
    /// Optional RE dump of runtime Block.blockID values (env 7DTD_DUMP_BLOCK_IDS=1).
    /// Stock AssignIds: terrain fills low ids, non-terrain starts at 256; not XML order.
    /// Full dump writes id\tname lines (same shape as assets/fixtures/assignids_v314.txt).
    /// </summary>
    static class BlockIdDump
    {
        // Pin names always logged even if full enumeration fails.
        static readonly string[] PinNames =
        {
            "air", "terrStone", "terrBedrock", "terrDirt", "terrForestGround", "terrTopSoil",
            "plantShrub", "treeDeadPineLeaf", "treeDeadTree02", "treeWinterEverGreen",
            "treeOakSml01", "treePlainsTree", "plantHedge",
            "woodShapes", "shippingContainerDoorLeftRotatedBlue",
            "cntWoodenChestClosed", "cntWoodenChestOpen", "cntWoodWritableCrate",
            "cntDeskSafe", "cntHardenedChestInsecure", "cntLootChestHeroInsecure",
            "cntShippingCrateHero", "cntStorageGeneric", "cntCupboardCabinetRedTopLootHelper",
        };

        static bool _dumped;

        // Env cannot change mid-process; snapshot once like the other flags
        // instead of re-reading per hook call.
        static readonly bool _dumpEnabled = EnvFlags.VarIsSetOn("7DTD_DUMP_BLOCK_IDS");

        static void DumpOnce(string reason)
        {
            if (!_dumpEnabled) return;
            if (_dumped) return;
            try
            {
                if (!Block.BlocksLoaded) return;
            }
            catch (Exception ex)
            {
                // An always-throwing probe would otherwise disable the
                // requested dump with no trace; announce once, keep retrying
                // on later hook fires.
                ProbeFailure.Once("BlockIdDump blocks-loaded probe", ex);
                return;
            }
            _dumped = true;
            Log.Out("[7dtd-fastconnect] BlockIdDump reason=" + reason);

            // Pins first (always visible in client log).
            foreach (var name in PinNames)
            {
                int id = LookupId(name);
                Log.Out("[7dtd-fastconnect] Block.id name=" + name + " id=" + id);
            }

            int written = DumpAllBlocks();
            Log.Out("[7dtd-fastconnect] BlockIdDump full rows=" + written);
        }

        static int LookupId(string name)
        {
            try
            {
                var bv = Block.GetBlockValue(name, true);
                return bv.type;
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] Block.id fail name=" + name + " " + ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// Enumerate every loaded Block and write name\tid lines.
        /// Prefer Block.nameToBlock / list when present; fall back to pins only.
        /// </summary>
        static int DumpAllBlocks()
        {
            string outPath = Environment.GetEnvironmentVariable("7DTD_DUMP_BLOCK_IDS_PATH");
            if (string.IsNullOrEmpty(outPath))
                outPath = Path.Combine(UserDirs.ProfileDir(), "zdtd_assignids_dump.txt");

            var rows = new SortedDictionary<int, string>();
            try
            {
                CollectFromStaticMaps(rows);
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] BlockIdDump map walk: " + ex.Message);
            }

            if (rows.Count == 0)
            {
                // Last resort: resolve pin names only (same as old dump).
                foreach (var name in PinNames)
                {
                    int id = LookupId(name);
                    if (id >= 0 && !rows.ContainsKey(id)) rows[id] = name;
                }
            }

            try
            {
                var sb = new StringBuilder(rows.Count * 40);
                foreach (var kv in rows)
                {
                    // Fixture format used by maxdamage.mergeAssignIdsDump: "id\tname" or "name=id".
                    sb.Append(kv.Key);
                    sb.Append('\t');
                    sb.Append(kv.Value);
                    sb.Append('\n');
                }
                File.WriteAllText(outPath, sb.ToString());
                Log.Out("[7dtd-fastconnect] BlockIdDump wrote " + rows.Count + " rows → " + outPath);
            }
            catch (Exception ex)
            {
                Log.Warning("[7dtd-fastconnect] BlockIdDump write failed (" + outPath + "): " + ex.Message);
            }
            return rows.Count;
        }

        static void CollectFromStaticMaps(SortedDictionary<int, string> rows)
        {
            var bt = typeof(Block);
            // Common stock field names across 7DTD builds.
            string[] mapNames =
            {
                "nameToBlock", "NameToBlock", "nameToBlockMap",
                "fullNameToBlock", "blocks", "list",
            };
            foreach (var fn in mapNames)
            {
                var fi = bt.GetField(fn, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi == null) continue;
                var obj = fi.GetValue(null);
                if (obj == null) continue;
                if (TryWalkDict(obj, rows)) return;
                if (TryWalkList(obj, rows)) return;
            }

            // Property fallbacks.
            string[] propNames = { "nameToBlock", "NameToBlock", "Blocks" };
            foreach (var pn in propNames)
            {
                var pi = bt.GetProperty(pn, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi == null) continue;
                var obj = pi.GetValue(null, null);
                if (obj == null) continue;
                if (TryWalkDict(obj, rows)) return;
                if (TryWalkList(obj, rows)) return;
            }
        }

        static bool TryWalkDict(object obj, SortedDictionary<int, string> rows)
        {
            var dict = obj as IDictionary;
            if (dict == null) return false;
            int before = rows.Count;
            foreach (DictionaryEntry de in dict)
            {
                if (de.Value == null) continue;
                // key=name value=Block, or key=id value=Block
                if (de.Key is string name)
                {
                    int id = BlockIdOf(de.Value);
                    if (id >= 0) rows[id] = name;
                }
                else
                {
                    TryAddBlockInstance(de.Value, rows);
                }
            }
            return rows.Count > before;
        }

        static bool TryWalkList(object obj, SortedDictionary<int, string> rows)
        {
            var list = obj as IEnumerable;
            if (list == null || obj is string) return false;
            if (obj is IDictionary) return false;
            int before = rows.Count;
            foreach (var item in list)
            {
                if (item == null) continue;
                TryAddBlockInstance(item, rows);
            }
            return rows.Count > before;
        }

        static void TryAddBlockInstance(object b, SortedDictionary<int, string> rows)
        {
            int id = BlockIdOf(b);
            string name = BlockNameOf(b);
            if (id < 0 || string.IsNullOrEmpty(name)) return;
            if (!rows.ContainsKey(id)) rows[id] = name;
        }

        static int BlockIdOf(object b)
        {
            if (b == null) return -1;
            var t = b.GetType();
            // Prefer blockID / BlockID field/property.
            foreach (var n in new[] { "blockID", "BlockID", "blockId" })
            {
                var fi = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi != null && fi.FieldType == typeof(int)) return (int)fi.GetValue(b);
                var pi = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi != null && pi.PropertyType == typeof(int)) return (int)pi.GetValue(b, null);
            }
            // Fallback GetBlockValue path via GetBlockName if present.
            try
            {
                string name = BlockNameOf(b);
                if (!string.IsNullOrEmpty(name))
                {
                    var bv = Block.GetBlockValue(name, true);
                    return bv.type;
                }
            }
            catch { }
            return -1;
        }

        static string BlockNameOf(object b)
        {
            if (b == null) return null;
            var t = b.GetType();
            foreach (var n in new[] { "blockName", "BlockName", "name", "Name" })
            {
                var fi = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi != null && fi.FieldType == typeof(string)) return (string)fi.GetValue(b);
                var pi = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi != null && pi.PropertyType == typeof(string))
                {
                    try { return (string)pi.GetValue(b, null); } catch { }
                }
            }
            // GetBlockName() method
            var mi = t.GetMethod("GetBlockName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (mi != null && mi.ReturnType == typeof(string))
            {
                try { return (string)mi.Invoke(b, null); } catch { }
            }
            return null;
        }

        [HarmonyPatch(typeof(Block), nameof(Block.AssignIds))]
        static class Patch_AssignIds
        {
            static void Postfix()
            {
                DumpOnce("AssignIds");
            }
        }

        [HarmonyPatch(typeof(XUiC_MainMenu), "OnOpen")]
        static class Patch_MainMenu
        {
            static void Postfix()
            {
                DumpOnce("MainMenu.OnOpen");
            }
        }
    }
}
