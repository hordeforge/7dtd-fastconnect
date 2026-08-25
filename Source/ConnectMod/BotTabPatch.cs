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

        // Announce-once channel shared by every guarded section below. The
        // inner per-section catches swallow locally by design (one bad row
        // must not kill the rest), but without this hook a persistently dead
        // patch (reflection drift after a game update) would look like an
        // empty player list working fine; ProbeFailure announces the first
        // failure once, then mutes.
        static void WarnOnce(string where, Exception ex)
        {
            ProbeFailure.Once("BotTabPatch " + where, ex);
        }

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
                catch (Exception ex)
                {
                    ProbeFailure.Once("BotTabPatch comparator resolve", ex);
                }
                if (_comparator == null)
                {
                    // Covers the throw above (muted by the once channel) and
                    // the rename case GetMethod cannot throw for: either way
                    // stock ordering is gone after a game update and bot rows
                    // fall back to name order, which must not pass silently.
                    ProbeFailure.Once(
                        "BotTabPatch stock PlayerComparator unavailable; bot rows sort by name",
                        "XUiC_PlayersList.PlayerComparator did not resolve");
                }
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

                var bots = CollectBots(GameManager.Instance.World);
                if (bots.Count == 0) return;

                var sorted = SortedListField?.GetValue(__instance) as List<PersistentPlayerData>;
                if (sorted == null) return;

                if (AppendMissingBots(sorted, bots) == 0) return;

                SortInjectedBots(sorted);
                UpdateCountAndPager(__instance, sorted.Count);
                BindTailRows(__instance, sorted);
            }
            catch (Exception ex)
            {
                // This postfix runs every 0.25s while Tab is open; a persistent
                // failure must not flood the log, but silence would look like
                // an empty player list working fine. Announce once.
                WarnOnce("list inject", ex);
            }
        }

        // Collect alive bot entities in the world: [Bot]-prefixed names, or
        // BotMod's buff marker when the name check does not decide.
        static List<EntityAlive> CollectBots(World world)
        {
            var bots = new List<EntityAlive>();
            try
            {
                var alives = world.EntityAlives;
                if (alives == null) return bots;
                foreach (var ea in alives)
                {
                    if (ea == null || ea.IsDead()) continue;
                    if (IsBot(ea)) bots.Add(ea);
                }
            }
            catch (Exception ex)
            {
                // A scan that always fails means zero bots are ever found;
                // that must not be silent.
                WarnOnce("bot scan", ex);
            }
            return bots;
        }

        static bool IsBot(EntityAlive ea)
        {
            // Identify by [Bot] prefix (server sets EntityName = [Bot] Foo_NN)
            string nm = null;
            try { nm = ea.EntityName; } catch { }
            if (!string.IsNullOrEmpty(nm) && nm.StartsWith("[Bot]", StringComparison.Ordinal))
                return true;
            // Fallback: buff marker set by BotMod (also tried when the name
            // is present but unprefixed).
            try
            {
                return ea.Buffs != null && ea.Buffs.HasCustomVar("botmod_isBot")
                    && ea.Buffs.GetCustomVar("botmod_isBot") > 0.5f;
            }
            catch { return false; }
        }

        static bool SortedContainsEntity(List<PersistentPlayerData> sorted, int entityId)
        {
            foreach (var ppd in sorted)
            {
                try { if (ppd != null && ppd.EntityId == entityId) return true; } catch { }
            }
            return false;
        }

        // Appends a synthetic PPD for every bot not already represented
        // (some server configs may map bots to PPL already); returns how many
        // were added.
        static int AppendMissingBots(List<PersistentPlayerData> sorted, List<EntityAlive> bots)
        {
            int added = 0;
            foreach (var bot in bots)
            {
                if (SortedContainsEntity(sorted, bot.entityId)) continue;
                var ppdBot = MakeBotPersistentPlayerData(bot);
                if (ppdBot == null) continue;
                sorted.Add(ppdBot);
                added++;
            }
            return added;
        }

        // Re-sort to keep deterministic order (bots after players, alphabetical).
        static void SortInjectedBots(List<PersistentPlayerData> sorted)
        {
            try
            {
                var comp = PlayerComparator();
                if (comp != null)
                    sorted.Sort(comp);
                else
                    sorted.Sort((a, b) => string.Compare(a?.PlayerName?.DisplayName, b?.PlayerName?.DisplayName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                WarnOnce("sort", ex);
            }
        }

        // Update the count label and paging to reflect the new size.
        static void UpdateCountAndPager(XUiC_PlayersList __instance, int totalCount)
        {
            try
            {
                var lbl = NumPlayersField?.GetValue(__instance) as XUiV_Label;
                if (lbl != null) lbl.Text = totalCount.ToString();
                var pg = PagerField?.GetValue(__instance) as XUiC_Paging;
                var gv = GridField?.GetValue(__instance) as XUiV_Grid;
                if (pg != null && gv != null) pg.SetLastPageByElementsAndPageLength(totalCount, gv.Rows);
            }
            catch (Exception ex)
            {
                WarnOnce("pager", ex);
            }
        }

        // Vanilla's binding pass already ran; fill the tail rows the appended
        // bots landed in. Vanilla's first pass bound 0..min(sorted.Count,
        // rows+page*rows). Bots appended extend sorted beyond what was bound,
        // so tail rows are empty.
        static void BindTailRows(XUiC_PlayersList __instance, List<PersistentPlayerData> sorted)
        {
            XUiC_PlayersListEntry[] entries;
            int start;
            try
            {
                entries = EntriesField?.GetValue(__instance) as XUiC_PlayersListEntry[];
                if (entries == null) return;
                var pager = PagerField?.GetValue(__instance) as XUiC_Paging;
                int rows;
                try
                {
                    var gv = GridField?.GetValue(__instance) as XUiV_Grid;
                    rows = gv != null ? gv.Rows : entries.Length;
                }
                catch { rows = entries.Length; }
                int page = pager != null ? pager.GetPage() : 0;
                start = page * rows;
            }
            catch (Exception ex)
            {
                WarnOnce("row bind", ex);
                return;
            }

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
                    var worldEnt = GameManager.Instance.World.GetEntity(ppd.EntityId) as EntityAlive;
                    // Same predicate CollectBots used to admit this entity:
                    // a buff-marker bot without the [Bot] name prefix would
                    // otherwise keep the tail row appended for it blank.
                    if (worldEnt == null || !IsBot(worldEnt))
                        continue;
                    BindBotRow(entry, ppd, worldEnt);
                }
                catch (Exception ex)
                {
                    // Per-row tolerance stays, but the first failure is
                    // announced: if every row fails the same way this is
                    // a broken patch, not a bad row.
                    WarnOnce("row bind", ex);
                }
            }
        }

        // Bind one row to a bot PPD: mimic vanilla online path but for zombie bots.
        static void BindBotRow(XUiC_PlayersListEntry entry, PersistentPlayerData ppd, EntityAlive worldEnt)
        {
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

        // Per-instance throttle state. ConditionalWeakTable so entries die
        // with the window: XUi recreates this list across world loads, and a
        // plain dictionary would accumulate destroyed instances forever.
        static readonly ConditionalWeakTable<XUiC_PlayersList, ThrottleState> _throttleStates =
            new ConditionalWeakTable<XUiC_PlayersList, ThrottleState>();

        sealed class ThrottleState
        {
            internal float LastRun;
        }

        static bool ShouldRun(XUiC_PlayersList inst)
        {
            float now = Time.unscaledTime;
            ThrottleState state = _throttleStates.GetOrCreateValue(inst);
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
                // Instant semantics: store as UTC so the timestamp does not
                // shift meaning when the host TZ or DST changes. Stock
                // PersistentPlayerList uses DateTime.Now (local wall time);
                // that drifts by one hour across a DST transition in the same
                // zone and by hours when a save moves between timezones.
                // PlayerComparator ignores LastLogin (orders by ally/level), so
                // this only keeps OfflineMinutes-style reads sane instead of
                // DateTime.MinValue, but recording the true instant avoids the
                // wall-clock bug for any future reader.
                ppd.LastLogin = DateTime.UtcNow;
                return ppd;
            }
            catch (Exception ex)
            {
                // If creation fails for every bot the list stays empty with no
                // visible cause; route through the shared announce-once hook.
                WarnOnce("bot ppd create", ex);
                return null;
            }
        }
    }
}
