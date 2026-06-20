using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using RayTraceAPI;

namespace ProOpeningReplay;

public sealed partial class ProOpeningReplayPlugin
{
    private void StopNativeReplay(ReplaySession session)
    {
        if (!session.NativeReplayActive)
        {
            return;
        }

        BotController.StopReplay(session.NativeReplaySlot);
        ReleaseNativeControllerState(session.NativeReplaySlot);
        session.NativeReplayActive = false;
        session.NativeReplaySlot = -1;
    }

    private void StopAllNativeReplays()
    {
        foreach (var session in _sessions)
        {
            StopNativeReplay(session);
        }
        ClearNativeWeaponState();
    }

    private void ClearRetakeMoveTos(bool releaseBots)
    {
        if (releaseBots)
        {
            foreach (var moveTo in _retakeMoveTos)
            {
                ReleaseBotToNativeAi(moveTo.Player);
            }
        }
        _retakeMoveTos.Clear();
    }

    private void ClearNativeWeaponState(int slot)
    {
        BotController.Unlock(slot, LockKind.Weapon);
        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
        _lastLockedWeaponTarget.Remove(slot);
        _preloadedReplayWeapons.RemoveWhere(entry => entry.Slot == slot);
    }

    private void ClearNativeWeaponState()
    {
        foreach (var slot in _lastLockedWeaponTarget.Keys.ToArray())
        {
            BotController.Unlock(slot, LockKind.Weapon);
        }
        _lastEnsuredWeaponDef.Clear();
        _lastReplayWeaponDef.Clear();
        _lastLockedWeaponTarget.Clear();
        _preloadedReplayWeapons.Clear();
    }

    private void ApplyReplaySideEffects(ReplaySession session)
    {
        var allowReplayAttack = false;
        if (session.NativeReplayActive)
        {
            var nativeCursor = BotController.GetReplayCursor(session.NativeReplaySlot);
            if (nativeCursor >= 0)
            {
                ApplyFreezeInventorySnapshots(session, nativeCursor);
            }
        }

        if (session.NativeReplayActive && BotController.TryGetReplayTick(session.NativeReplaySlot, out var tick))
        {
            var weaponDefIndex = NormalizeWeaponDefIndex(tick.WeaponDefIndex);
            var isThrowableUtility = BotController.IsThrowableUtilityWeaponDef(weaponDefIndex);
            ApplyReplayWeaponPreset(session, weaponDefIndex, allowSlotReplacement: false, force: false);
            if (isThrowableUtility && !IsReplayWeaponActive(session.Player, weaponDefIndex))
            {
                ApplyReplayWeaponPreset(session, weaponDefIndex, allowSlotReplacement: false, force: true);
            }

            allowReplayAttack = isThrowableUtility && IsReplayWeaponActive(session.Player, weaponDefIndex);
        }

        ApplyReplayControlSideEffects(session.Player, session.StartTime, allowReplayAttack);
    }

    private void ApplyFreezeInventorySnapshots(ReplaySession session, int replayTickIndex)
    {
        if (_freezeEnded
            || session.Kind != ReplaySessionKind.Opening
            || replayTickIndex < 0
            || session.ReplayPlayer.FreezeInventorySnapshots.Count == 0)
        {
            return;
        }

        while (session.NextFreezeInventorySnapshotIndex < session.ReplayPlayer.FreezeInventorySnapshots.Count)
        {
            var snapshot = session.ReplayPlayer.FreezeInventorySnapshots[session.NextFreezeInventorySnapshotIndex];
            if (FreezeSnapshotReplayTickIndex(session.ReplayPlayer, snapshot) > replayTickIndex)
            {
                break;
            }

            ApplyFreezeInventorySnapshot(session.Player, snapshot);
            session.NextFreezeInventorySnapshotIndex++;
        }
    }

    private static int FreezeSnapshotReplayTickIndex(ReplayPlayer replayPlayer, FreezeInventorySnapshot snapshot)
        => Math.Max(0, replayPlayer.FreezeEndTickIndex + snapshot.RelativeTick);

    private static void ApplyFreezeInventorySnapshot(CCSPlayerController player, FreezeInventorySnapshot snapshot)
    {
        if (!player.IsValid || !player.PawnIsAlive)
        {
            return;
        }

        if (snapshot.Inventory.Count > 0 || snapshot.InventoryDefIndexes.Count > 0)
        {
            SyncInventorySnapshotItems(player, BuildSnapshotItems(snapshot.Inventory, snapshot.InventoryDefIndexes));
            return;
        }

        GivePlayerInventoryItems(player, BuildSnapshotItems(snapshot.Added, snapshot.AddedDefIndexes));
    }

    private static Dictionary<string, int> BuildSnapshotItems(IEnumerable<string> itemNames, IEnumerable<int> defIndexes)
    {
        var items = CountItems(itemNames
            .Select(NormalizeLoadoutItem)
            .Where(IsReplayLoadoutItem));
        MergeReplayLoadoutDefs(items, defIndexes);
        return items;
    }

    private static void SyncInventorySnapshotItems(CCSPlayerController player, Dictionary<string, int> targetItems)
    {
        var currentItems = CountItems(GetCurrentInventory(player)
            .Select(NormalizeLoadoutItem)
            .Where(IsReplayLoadoutItem));

        EnsureDefaultPistolTarget(player, currentItems, targetItems);
        RemoveSurplusInventoryItems(player, currentItems, targetItems);
        GiveMissingInventorySnapshotItems(player, targetItems);
        EnsurePrimaryOrSecondaryFallback(player);
    }

    private static void GiveMissingInventorySnapshotItems(CCSPlayerController player, Dictionary<string, int> targetItems)
    {
        var currentItems = CountItems(GetCurrentInventory(player)
            .Select(NormalizeLoadoutItem)
            .Where(IsReplayLoadoutItem));
        var toGive = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (itemName, targetCount) in targetItems)
        {
            var missing = targetCount - currentItems.GetValueOrDefault(itemName);
            if (missing > 0)
            {
                toGive[itemName] = missing;
            }
        }

        GivePlayerInventoryItems(player, toGive);
        EnsurePrimaryOrSecondaryFallback(player);
    }

    private static void RemoveSurplusInventoryItems(
        CCSPlayerController player,
        Dictionary<string, int> currentItems,
        Dictionary<string, int> targetItems)
    {
        foreach (var (itemName, ownedCount) in currentItems)
        {
            var surplusCount = Math.Max(0, ownedCount - targetItems.GetValueOrDefault(itemName));
            if (surplusCount > 0)
            {
                RemoveInventoryItemsAndCleanupDrops(player, itemName, surplusCount);
            }
        }
    }

    private static void GivePlayerInventoryItems(CCSPlayerController player, Dictionary<string, int> items)
    {
        foreach (var (itemName, count) in items)
        {
            if (!IsReplayLoadoutItem(itemName))
            {
                continue;
            }

            for (var i = 0; i < count; i++)
            {
                TryGiveNamedItem(player, itemName);
            }
        }
    }

    private void ApplyReplayControlSideEffects(CCSPlayerController player, float startTime, bool allowReplayAttack)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return;
        }
        var bot = pawn.Bot;
        if (bot == null)
        {
            return;
        }

        // Suppress combat only
        if (_config.SuppressBotEngagementWhileReplaying)
        {
            bot.IsAttacking = false;
            bot.IsRapidFiring = false;
            bot.IsAimingAtEnemy = false;

            if (!allowReplayAttack)
            {
                bot.FireWeaponTimestamp = Server.CurrentTime + 0.5f;

                var ws = pawn.WeaponServices as CCSPlayer_WeaponServices;
                if (ws != null)
                {
                    ws.NextAttack = Server.CurrentTime + 0.5f;
                }
            }

            // Prevent stuck-recovery jumps
            ref bool isStuck = ref bot.IsStuck;
            isStuck = false;
            ref float jumpTimestamp = ref bot.JumpTimestamp;
            jumpTimestamp = Server.CurrentTime + 2.0f;
            CountdownTimer stuckJumpTimer = bot.StuckJumpTimer;
            ref float stuckJumpDur = ref stuckJumpTimer.Duration;
            stuckJumpDur = 2.0f;
            ref float stuckJumpTs = ref stuckJumpTimer.Timestamp;
            stuckJumpTs = Server.CurrentTime + 2.0f;

            if (!_config.KeepBotPerceptionDuringReplay)
            {
                // After 20s of replay, allow perception so bots react to sounds.
                var elapsed = Server.CurrentTime - startTime;
                if (elapsed < 20f)
                {
                    CountdownTimer ignoreEnemiesTimer = bot.IgnoreEnemiesTimer;
                    ref float ignoreDuration = ref ignoreEnemiesTimer.Duration;
                    ignoreDuration = 0.5f;
                    ref float ignoreTimestamp = ref ignoreEnemiesTimer.Timestamp;
                    ignoreTimestamp = Server.CurrentTime + 0.5f;
                    ref float ignoreScale = ref ignoreEnemiesTimer.Timescale;
                    ignoreScale = 1.0f;
                }
                else
                {
                    CountdownTimer ignoreEnemiesTimer = bot.IgnoreEnemiesTimer;
                    ref float ignoreDuration = ref ignoreEnemiesTimer.Duration;
                    ignoreDuration = 0f;
                    ref float ignoreTimestamp = ref ignoreEnemiesTimer.Timestamp;
                    ignoreTimestamp = 0f;
                }
            }
            else
            {
                // Make sure no leftover ignore window is still ticking from a prior frame -- we want
                // perception to register sights/sounds as they happen.
                CountdownTimer ignoreEnemiesTimer = bot.IgnoreEnemiesTimer;
                ref float ignoreDuration = ref ignoreEnemiesTimer.Duration;
                ignoreDuration = 0f;
                ref float ignoreTimestamp = ref ignoreEnemiesTimer.Timestamp;
                ignoreTimestamp = 0f;
            }
        }
    }

    private static string NormalizeGrenadeType(string grenadeType)
    {
        return GrenadeTypeAliases.GetValueOrDefault(grenadeType, grenadeType);
    }
}
