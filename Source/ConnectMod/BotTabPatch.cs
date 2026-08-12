using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ZdtdConnect
{
    /// <summary>
    /// Append FPS bots (zombieSoldier with [Bot] prefix) to the Tab player list.
    /// Works with dedicated BotMod: bots are EntityZombie, so vanilla Tab never shows them.
    /// This postfix scans the world for alive entities whose EntityName starts with [Bot]
    /// and injects synthetic rows into XUiC_PlayersList by expanding its backing sortedPlayerList
    /// via reflection-constructed PersistentPlayerData. Also covers server-side ConsoleCmdListPlayers (lp).
    /// Client-only; dedicated server does not have XUi.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_PlayersList), "updatePlayersList")]
    static class Patch_PlayersList_Bots
    {
        static void Postfix(XUiC_PlayersList __instance)
        {
            try
            {
                if (__instance == null) return;
                if (GameManager.Instance == null || GameManager.Instance.World == null) return;
                // Limit frequency: XUi calls this every frame while tab is open; throttle via time.
                // Use a per-instance time check stored in a static dict.
                if (!ShouldRun(__instance)) return;

                var ppl = GameManager.Instance.persistentPlayers;
                var world = GameManager.Instance.World;

                // Collect bot entities in world: EntityAlive zombie with [Bot] prefix
                var bots = new List<EntityAlive>();
                try
                {
                    // World.EntityAlives contains all alive; filter to bots
                    var alives = world.EntityAlives;
                    if (alives != null)
                    {
                        foreach (var ea in alives)
                        {
                            if (ea == null || ea.IsDead()) continue;
                            // Identify by [Bot] prefix (server sets EntityName = [Bot] Foo_NN)
                            string nm = null;
                            try { nm = ea.EntityName; } catch { }
                            if (!string.IsNullOrEmpty(nm) && nm.StartsWith("[Bot]", StringComparison.Ordinal))
                                bots.Add(ea);
                            else
                            {
                                // Fallback: buff marker set by BotMod
                                try
                                {
                                    if (ea.Buffs != null && ea.Buffs.HasCustomVar("botmod_isBot") && ea.Buffs.GetCustomVar("botmod_isBot") > 0.5f)
                                        bots.Add(ea);
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }

                if (bots.Count == 0) return;

                // Access private sortedPlayerList via reflection to append synthetic entries.
                // We'll create a PersistentPlayerData per bot and add to the list that XUi just sorted/counted.
                // The vanilla loop after sortedPlayerList sorts has already run; we need to rebind rows.
                // Approach: inject into sortedPlayerList then force a second layout pass by calling a helper via reflection,
                // or simpler: directly populate the visible row entries that are still empty.
                var listField = typeof(XUiC_PlayersList).GetField("sortedPlayerList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                var entriesField = typeof(XUiC_PlayersList).GetField("playerEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (listField == null) return;
                var sorted = listField.GetValue(__instance) as List<PersistentPlayerData>;
                if (sorted == null) return;

                int added = 0;
                foreach (var bot in bots)
                {
                    // Avoid duplicate if already represented (some server configs may map bots to PPL already)
                    bool already = false;
                    foreach (var ppd in sorted)
                    {
                        try { if (ppd != null && ppd.EntityId == bot.entityId) { already = true; break; } } catch { }
                    }
                    if (already) continue;

                    var ppdBot = MakeBotPersistentPlayerData(bot);
                    if (ppdBot == null) continue;
                    sorted.Add(ppdBot);
                    added++;
                }

                if (added == 0) return;

                // Re-sort to keep deterministic order (bots after players, alphabetical)
                try
                {
                    var comp = typeof(XUiC_PlayersList).GetMethod("PlayerComparator", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (comp != null)
                    {
                        Comparison<PersistentPlayerData> del = (Comparison<PersistentPlayerData>)Delegate.CreateDelegate(typeof(Comparison<PersistentPlayerData>), comp);
                        sorted.Sort(del);
                    }
                    else
                    {
                        sorted.Sort((a, b) => string.Compare(a?.PlayerName?.DisplayName, b?.PlayerName?.DisplayName, StringComparison.OrdinalIgnoreCase));
                    }
                }
                catch { }

                // Update the count label and paging to reflect new size
                try
                {
                    var numLabel = typeof(XUiC_PlayersList).GetField("numberOfPlayers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    var pager = typeof(XUiC_PlayersList).GetField("playerPager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    var grid = typeof(XUiC_PlayersList).GetField("playerList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    var lbl = numLabel?.GetValue(__instance) as XUiV_Label;
                    if (lbl != null) lbl.Text = sorted.Count.ToString();
                    var pg = pager?.GetValue(__instance) as XUiC_Paging;
                    var gv = grid?.GetValue(__instance) as XUiV_Grid;
                    if (pg != null && gv != null) pg.SetLastPageByElementsAndPageLength(sorted.Count, gv.Rows);
                }
                catch { }

                // Reuse vanilla row-binding logic by re-invoking the binding loop via a second call?
                // Easiest: call updatePlayersList again with a reentrancy guard (Postfix will early-exit second time if bots already added).
                // Instead, manually fill the remaining empty row slots with bot data using same visual logic vanilla uses.
                try
                {
                    var entries = entriesField?.GetValue(__instance) as XUiC_PlayersListEntry[];
                    if (entries == null) return;
                    var pager = typeof(XUiC_PlayersList).GetField("playerPager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(__instance) as XUiC_Paging;
                    int rows = 0;
                    try { var gv2 = typeof(XUiC_PlayersList).GetField("playerList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(__instance) as XUiV_Grid; rows = gv2 != null ? gv2.Rows : entries.Length; } catch { rows = entries.Length; }
                    int page = pager != null ? pager.GetPage() : 0;
                    int start = page * rows;
                    // Find first empty slot and fill sequentially with overflow bots that didn't fit in first page due to prior binding.
                    // Vanilla's first pass bound 0..min(sorted.Count, rows+page*rows). Bots appended extend sorted beyond what was bound, so tail rows are empty.
                    for (int i = 0; i < entries.Length; i++)
                    {
                        var entry = entries[i];
                        if (entry == null) continue;
                        int idx = start + i;
                        if (idx >= sorted.Count) break;
                        var ppd = sorted[idx];
                        // If this row already shows correct entity (via EntityId), skip
                        try
                        {
                            var curId = (int)typeof(XUiC_PlayersListEntry).GetField("EntityId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(entry);
                            if (curId == ppd.EntityId && curId != -1) continue;
                            // Check if row is empty (EntityId == -1 and PlayerData == null before our injection) or mismatched bot
                            var worldEnt = world.GetEntity(ppd.EntityId) as EntityAlive;
                            if (worldEnt != null && worldEnt.EntityName != null && worldEnt.EntityName.StartsWith("[Bot]", StringComparison.Ordinal))
                            {
                                // Bind this row to bot ppd: mimic vanilla online path but for zombie bots
                                typeof(XUiC_PlayersListEntry).GetField("EntityId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).SetValue(entry, ppd.EntityId);
                                typeof(XUiC_PlayersListEntry).GetField("PlayerData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).SetValue(entry, ppd);
                                entry.ViewComponent.IsVisible = true;
                                var pn = typeof(XUiC_PlayersListEntry).GetField("PlayerName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(entry) as XUiC_PlayerName;
                                if (pn != null) pn.UpdatePlayerData(ppd.PlayerData, false, ppd.PlayerName.DisplayName);
                                typeof(XUiC_PlayersListEntry).GetField("IsOffline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.SetValue(entry, false);
                                typeof(XUiC_PlayersListEntry).GetProperty("IsLocalPlayer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.SetValue(entry, false, null);
                                // Stats: show bot's alive stats (health/level trivially)
                                var zk = typeof(XUiC_PlayersListEntry).GetField("ZombieKillsText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(entry) as XUiV_Label;
                                var pk = typeof(XUiC_PlayersListEntry).GetField("PlayerKillsText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(entry) as XUiV_Label;
                                var de = typeof(XUiC_PlayersListEntry).GetField("DeathsText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(entry) as XUiV_Label;
                                var lv = typeof(XUiC_PlayersListEntry).GetField("LevelText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(entry) as XUiV_Label;
                                var gs = typeof(XUiC_PlayersListEntry).GetField("GamestageText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(entry) as XUiV_Label;
                                var ping = typeof(XUiC_PlayersListEntry).GetField("PingText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(entry) as XUiV_Label;
                                if (zk != null) zk.Text = worldEnt.KilledZombies.ToString();
                                if (pk != null) pk.Text = worldEnt.KilledPlayers.ToString();
                                if (de != null) de.Text = worldEnt.Died.ToString();
                                if (lv != null) lv.Text = (worldEnt.Progression != null ? worldEnt.Progression.GetLevel() : 1).ToString();
                                if (gs != null) gs.Text = (worldEnt is EntityPlayer ep ? ep.gameStage : 0).ToString();
                                if (ping != null) ping.Text = "--"; // bots have no ping
                                // Hide moderation/party UI for bots
                                var admin = typeof(XUiC_PlayersListEntry).GetField("AdminSprite", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(entry) as XUiV_Sprite;
                                if (admin != null) admin.IsVisible = false;
                                var voice = typeof(XUiC_PlayersListEntry).GetField("Voice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(entry) as XUiV_Button;
                                if (voice != null) voice.IsVisible = false;
                                var chat = typeof(XUiC_PlayersListEntry).GetField("Chat", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(entry) as XUiV_Button;
                                if (chat != null) chat.IsVisible = false;
                                entry.RefreshBindings();
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // Throttle log
                // Log.Out($"[zdtd-connect] Tab bots injected +{added} total={sorted.Count}");
            }
            catch (Exception ex)
            {
                try { Log.Warning("[zdtd-connect] BotTabPatch failed: " + ex.Message); } catch { }
            }
        }

        static readonly Dictionary<XUiC_PlayersList, float> _last = new Dictionary<XUiC_PlayersList, float>();
        static bool ShouldRun(XUiC_PlayersList inst)
        {
            float now = Time.unscaledTime;
            float last;
            if (_last.TryGetValue(inst, out last) && now - last < 0.25f) return false;
            _last[inst] = now;
            return true;
        }

        static PersistentPlayerData MakeBotPersistentPlayerData(EntityAlive bot)
        {
            try
            {
                string name = bot.EntityName ?? ("[Bot] " + bot.entityId);
                if (!name.StartsWith("[Bot]", StringComparison.Ordinal)) name = "[Bot] " + name;
                // Create a synthetic platform identifier that will not collide with real players.
                // Use a long-based Steam id in bot range: 90000000000000000 + entityId
                ulong fakeSteamId = 90000000000000000UL + (ulong)(bot.entityId & 0x7FFFFFFF);
                var id = new Platform.Steam.UserIdentifierSteam(fakeSteamId.ToString());
                AuthoredText at = new AuthoredText(name, id);
                // Also set EntityId on the PPD so Tab's GetEntity(ppd.EntityId) can resolve the bot.
                var ppd = new PersistentPlayerData(id, id, at, Platform.EPlayGroup.Standalone);
                ppd.EntityId = bot.entityId;
                // LastLogin now so sort is stable
                ppd.LastLogin = DateTime.Now;
                return ppd;
            }
            catch { return null; }
        }
    }
}
