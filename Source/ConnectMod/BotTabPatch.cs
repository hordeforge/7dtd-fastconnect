using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace SdtdConnect
{
    /// <summary>
    /// Append FPS bots (zombieSoldier with [Bot] prefix) to the Tab player list.
    /// Works with dedicated BotMod: bots are EntityZombie, so vanilla Tab never shows them.
    /// This postfix scans the world for alive entities whose EntityName starts with [Bot]
    /// and injects synthetic rows into XUiC_PlayersList by expanding its backing sortedPlayerList
    /// via reflection-constructed PersistentPlayerData.
    /// Client-only; dedicated server does not have XUi.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_PlayersList), "updatePlayersList")]
    static class Patch_PlayersList_Bots
    {
        // Stock reflection targets resolved once: updatePlayersList fires every
        // frame while Tab is open, so re-walking metadata per refresh is waste.
        static readonly FieldInfo SortedListField =
            InstanceField(typeof(XUiC_PlayersList), "sortedPlayerList");
        static readonly FieldInfo EntriesField =
            InstanceField(typeof(XUiC_PlayersList), "playerEntries");
        static readonly FieldInfo NumPlayersField =
            InstanceField(typeof(XUiC_PlayersList), "numberOfPlayers");
        static readonly FieldInfo PagerField =
            InstanceField(typeof(XUiC_PlayersList), "playerPager");
        static readonly FieldInfo GridField =
            InstanceField(typeof(XUiC_PlayersList), "playerList");

        static readonly FieldInfo EntryEntityIdField =
            InstanceField(typeof(XUiC_PlayersListEntry), "EntityId");
        static readonly FieldInfo EntryPlayerDataField =
            InstanceField(typeof(XUiC_PlayersListEntry), "PlayerData");
        static readonly FieldInfo EntryPlayerNameField =
            InstanceField(typeof(XUiC_PlayersListEntry), "PlayerName");
        static readonly FieldInfo EntryIsOfflineField =
            InstanceField(typeof(XUiC_PlayersListEntry), "IsOffline");
        static readonly PropertyInfo EntryIsLocalPlayerProp =
            typeof(XUiC_PlayersListEntry).GetProperty("IsLocalPlayer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo EntryAdminSpriteField =
            InstanceField(typeof(XUiC_PlayersListEntry), "AdminSprite");
        static readonly FieldInfo EntryVoiceField =
            InstanceField(typeof(XUiC_PlayersListEntry), "Voice");
        static readonly FieldInfo EntryChatField =
            InstanceField(typeof(XUiC_PlayersListEntry), "Chat");
        static readonly FieldInfo EntryZombieKillsField =
            InstanceField(typeof(XUiC_PlayersListEntry), "ZombieKillsText");
        static readonly FieldInfo EntryPlayerKillsField =
            InstanceField(typeof(XUiC_PlayersListEntry), "PlayerKillsText");
        static readonly FieldInfo EntryDeathsField =
            InstanceField(typeof(XUiC_PlayersListEntry), "DeathsText");
        static readonly FieldInfo EntryLevelField =
            InstanceField(typeof(XUiC_PlayersListEntry), "LevelText");
        static readonly FieldInfo EntryGamestageField =
            InstanceField(typeof(XUiC_PlayersListEntry), "GamestageText");
        static readonly FieldInfo EntryPingField =
            InstanceField(typeof(XUiC_PlayersListEntry), "PingText");

        static Comparison<PersistentPlayerData> _comparator;
        static bool _comparatorResolved;

        static FieldInfo InstanceField(Type t, string name)
        {
            return t.GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        static void SetLabel(FieldInfo field, object entry, string text)
        {
            var lbl = field?.GetValue(entry) as XUiV_Label;
            if (lbl != null) lbl.Text = text;
        }

        static Comparison<PersistentPlayerData> PlayerComparator()
        {
            if (!_comparatorResolved)
            {
                _comparatorResolved = true;
                try
                {
                    var mi = typeof(XUiC_PlayersList).GetMethod("PlayerComparator",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mi != null)
                        _comparator = (Comparison<PersistentPlayerData>)Delegate.CreateDelegate(
                            typeof(Comparison<PersistentPlayerData>), mi);
                }
                catch { _comparator = null; }
            }
            return _comparator;
        }
        static void Postfix(XUiC_PlayersList __instance)
        {
            try
            {
                if (__instance == null) return;
                if (GameManager.Instance == null || GameManager.Instance.World == null) return;
                // Limit frequency: XUi calls this every frame while tab is open; throttle via time.
                // Per-instance time check, keyed weakly so it cannot outlive the window.
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

                // Expand the backing sorted list via cached reflection handles.
                var sorted = SortedListField?.GetValue(__instance) as List<PersistentPlayerData>;
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
                    var comp = PlayerComparator();
                    if (comp != null)
                        sorted.Sort(comp);
                    else
                        sorted.Sort((a, b) => string.Compare(a?.PlayerName?.DisplayName, b?.PlayerName?.DisplayName, StringComparison.OrdinalIgnoreCase));
                }
                catch { }

                // Update the count label and paging to reflect new size
                try
                {
                    var lbl = NumPlayersField?.GetValue(__instance) as XUiV_Label;
                    if (lbl != null) lbl.Text = sorted.Count.ToString();
                    var pg = PagerField?.GetValue(__instance) as XUiC_Paging;
                    var gv = GridField?.GetValue(__instance) as XUiV_Grid;
                    if (pg != null && gv != null) pg.SetLastPageByElementsAndPageLength(sorted.Count, gv.Rows);
                }
                catch { }

                // Vanilla's binding pass already ran; fill the tail rows the appended bots landed in.
                try
                {
                    var entries = EntriesField?.GetValue(__instance) as XUiC_PlayersListEntry[];
                    if (entries == null) return;
                    var pager = PagerField?.GetValue(__instance) as XUiC_Paging;
                    int rows;
                    try
                    {
                        var gv2 = GridField?.GetValue(__instance) as XUiV_Grid;
                        rows = gv2 != null ? gv2.Rows : entries.Length;
                    }
                    catch { rows = entries.Length; }
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
                            var curId = EntryEntityIdField != null ? (int)EntryEntityIdField.GetValue(entry) : -1;
                            if (curId == ppd.EntityId && curId != -1) continue;
                            // Check if row is empty (EntityId == -1 and PlayerData == null before our injection) or mismatched bot
                            var worldEnt = world.GetEntity(ppd.EntityId) as EntityAlive;
                            if (worldEnt == null || worldEnt.EntityName == null
                                || !worldEnt.EntityName.StartsWith("[Bot]", StringComparison.Ordinal))
                                continue;

                            // Bind this row to bot ppd: mimic vanilla online path but for zombie bots
                            EntryEntityIdField?.SetValue(entry, ppd.EntityId);
                            EntryPlayerDataField?.SetValue(entry, ppd);
                            entry.ViewComponent.IsVisible = true;
                            var pn = EntryPlayerNameField?.GetValue(entry) as XUiC_PlayerName;
                            if (pn != null) pn.UpdatePlayerData(ppd.PlayerData, false, ppd.PlayerName.DisplayName);
                            EntryIsOfflineField?.SetValue(entry, false);
                            EntryIsLocalPlayerProp?.SetValue(entry, false, null);
                            // Stats: show bot's alive stats (health/level trivially)
                            SetLabel(EntryZombieKillsField, entry, worldEnt.KilledZombies.ToString());
                            SetLabel(EntryPlayerKillsField, entry, worldEnt.KilledPlayers.ToString());
                            SetLabel(EntryDeathsField, entry, worldEnt.Died.ToString());
                            SetLabel(EntryLevelField, entry,
                                (worldEnt.Progression != null ? worldEnt.Progression.GetLevel() : 1).ToString());
                            SetLabel(EntryGamestageField, entry,
                                (worldEnt is EntityPlayer ep ? ep.gameStage : 0).ToString());
                            SetLabel(EntryPingField, entry, "--"); // bots have no ping
                            // Hide moderation/party UI for bots
                            var admin = EntryAdminSpriteField?.GetValue(entry) as XUiV_Sprite;
                            if (admin != null) admin.IsVisible = false;
                            var voice = EntryVoiceField?.GetValue(entry) as XUiV_Button;
                            if (voice != null) voice.IsVisible = false;
                            var chat = EntryChatField?.GetValue(entry) as XUiV_Button;
                            if (chat != null) chat.IsVisible = false;
                            entry.RefreshBindings();
                        }
                        catch { }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                try { Log.Warning("[7dtd-connect] BotTabPatch failed: " + ex.Message); } catch { }
            }
        }

        // Per-instance throttle state. ConditionalWeakTable so entries die
        // with the window: XUi recreates this list across world loads, and a
        // plain dictionary would accumulate destroyed instances forever.
        static readonly ConditionalWeakTable<XUiC_PlayersList, ThrottleState> _last =
            new ConditionalWeakTable<XUiC_PlayersList, ThrottleState>();

        sealed class ThrottleState
        {
            internal float LastRun;
        }

        static bool ShouldRun(XUiC_PlayersList inst)
        {
            float now = Time.unscaledTime;
            ThrottleState state = _last.GetOrCreateValue(inst);
            if (now - state.LastRun < 0.25f) return false;
            state.LastRun = now;
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
                // Engine-convention local wall-clock stamp: PersistentPlayerList
                // also sets LastLogin = DateTime.Now, and OfflineMinutes-style
                // reads subtract it from DateTime.Now. PlayerComparator ignores
                // LastLogin (it orders by ally/level), so this only keeps such
                // reads sane instead of seeing DateTime.MinValue.
                ppd.LastLogin = DateTime.Now;
                return ppd;
            }
            catch { return null; }
        }
    }
}
