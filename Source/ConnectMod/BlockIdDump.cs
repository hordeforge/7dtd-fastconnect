using System;
using System.Collections.Generic;
using System.IO;
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
        /// Enumerate every loaded Block and write name\tid lines from
        /// Block.nameToBlock (public static Dictionary&lt;string, Block&gt;),
        /// falling back to pins only when it yields nothing.
        /// </summary>
        static int DumpAllBlocks()
        {
            string outPath = Environment.GetEnvironmentVariable("7DTD_DUMP_BLOCK_IDS_PATH");
            if (string.IsNullOrEmpty(outPath))
                outPath = Path.Combine(UserDirs.ProfileDir(), "zdtd_assignids_dump.txt");

            var rows = new SortedDictionary<int, string>();
            try
            {
                foreach (var kv in Block.nameToBlock)
                {
                    if (kv.Value == null || kv.Value.blockID < 0) continue;
                    rows[kv.Value.blockID] = kv.Key;
                }
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
