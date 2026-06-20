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
    private bool ApplyLoadout(CCSPlayerController player, ReplayPlayer replayPlayer, int loadoutBudget)
    {
        if (!player.IsValid || player.InGameMoneyServices == null)
        {
            return false;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return false;
        }

        var itemServices = pawn.ItemServices != null && pawn.ItemServices.Handle != IntPtr.Zero
            ? new CCSPlayer_ItemServices(pawn.ItemServices.Handle)
            : null;

        pawn.ArmorValue = replayPlayer.ArmorValue;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        if (itemServices != null)
        {
            itemServices.HasHelmet = replayPlayer.HasHelmet;
            itemServices.HasDefuser = player.Team == CsTeam.CounterTerrorist && replayPlayer.HasDefuser;
        }

        var targetItems = BuildReplayLoadoutItems(replayPlayer);
        var deferredWeaponSync = false;
        deferredWeaponSync |= SyncTargetWeaponSlot(player, targetItems, ReplayWeaponSlot.Primary, IsPrimaryWeapon, allowReplacement: !_freezeEnded);
        deferredWeaponSync |= SyncTargetWeaponSlot(player, targetItems, ReplayWeaponSlot.Secondary, IsSecondaryWeapon, allowReplacement: !_freezeEnded);
        GiveMissingTargetItemsDirect(player, targetItems, itemName => !IsPrimaryWeapon(itemName) && !IsSecondaryWeapon(itemName));
        if (deferredWeaponSync)
        {
            Server.NextFrame(() => Server.NextFrame(() => SwitchToReplayLoadoutStartWeapon(player, replayPlayer)));
        }
        else
        {
            SwitchToReplayLoadoutStartWeapon(player, replayPlayer);
        }

        var loadoutValue = ReplayLoadoutValue(replayPlayer);
        player.InGameMoneyServices.Account = RoundMoneyDown(loadoutBudget - loadoutValue);
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
        return true;
    }

    private static void ApplyReplayBudgetMoney(CCSPlayerController player, ReplayAssignment assignment)
    {
        if (!player.IsValid || player.InGameMoneyServices == null)
        {
            return;
        }

        var loadoutValue = ReplayLoadoutValue(assignment.Player);
        player.InGameMoneyServices.Account = RoundMoneyDown(assignment.Budget - loadoutValue);
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
    }

    private bool SyncTargetWeaponSlot(
        CCSPlayerController player,
        Dictionary<string, int> targetItems,
        ReplayWeaponSlot slot,
        Func<string, bool> predicate,
        bool allowReplacement)
    {
        var targetItem = BestTargetSlotItem(targetItems, predicate);
        if (targetItem == null)
        {
            return false;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null)
        {
            TryGiveNamedItem(player, targetItem);
            return false;
        }

        if (HasReplayWeapon(pawn, targetItem))
        {
            if (allowReplacement)
            {
                RemoveConflictingReplaySlotWeapons(player, pawn, slot, targetItem);
            }
            return false;
        }

        var currentSlotWeapons = GetWeaponsInReplaySlot(pawn, slot).ToList();
        if (currentSlotWeapons.Count == 0)
        {
            TryGiveNamedItem(player, targetItem);
            return false;
        }

        if (!allowReplacement)
        {
            return false;
        }

        var fallbackItem = currentSlotWeapons
            .Select(weapon => NormalizeLoadoutItem(weapon.DesignerName))
            .FirstOrDefault(itemName => !WeaponClassMatches(itemName, targetItem));
        var weaponToDrop = currentSlotWeapons
            .FirstOrDefault(weapon => !WeaponClassMatches(
                NormalizeLoadoutItem(weapon.DesignerName),
                targetItem));
        if (fallbackItem == null || weaponToDrop == null)
        {
            return false;
        }

        if (!DropAndKillReplayWeapon(player, pawn, weaponToDrop, "replace_loadout_slot"))
        {
            return false;
        }

        if (player.Slot >= 0)
        {
            _lastEnsuredWeaponDef.Remove(player.Slot);
            _lastReplayWeaponDef.Remove(player.Slot);
        }

        Server.NextFrame(() => CompleteWeaponSlotReplacement(player, targetItem, fallbackItem, slot));
        return true;
    }

    private static string? BestTargetSlotItem(Dictionary<string, int> targetItems, Func<string, bool> predicate)
        => targetItems.Keys
            .Where(predicate)
            .OrderByDescending(ItemPrice)
            .ThenBy(itemName => itemName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private void CompleteWeaponSlotReplacement(
        CCSPlayerController player,
        string targetItem,
        string fallbackItem,
        ReplayWeaponSlot slot)
    {
        if (!player.IsValid)
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null)
        {
            return;
        }

        if (HasReplayWeapon(pawn, targetItem) || GetWeaponsInReplaySlot(pawn, slot).Any())
        {
            return;
        }

        TryGiveNamedItem(player, targetItem);
        Server.NextFrame(() => RestoreFallbackWeaponIfNeeded(player, targetItem, fallbackItem, slot));
    }

    private static void RestoreFallbackWeaponIfNeeded(
        CCSPlayerController player,
        string targetItem,
        string fallbackItem,
        ReplayWeaponSlot slot)
    {
        if (!player.IsValid)
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null)
        {
            return;
        }

        if (HasReplayWeapon(pawn, targetItem) || GetWeaponsInReplaySlot(pawn, slot).Any())
        {
            return;
        }

        TryGiveNamedItem(player, fallbackItem);
    }

    private static bool TryGiveNamedItem(CCSPlayerController player, string itemName)
    {
        try
        {
            player.GiveNamedItem(itemName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySelectWeapon(CCSPlayerController player, CCSPlayerPawn pawn, CBasePlayerWeapon weapon)
    {
        if (player.Slot >= 0)
        {
            var defIndex = WeaponDefIndex(weapon.DesignerName);
            if (defIndex >= 0)
            {
                BotController.SwitchBotWeapon(player.Slot, NativeWeaponDefIndex(defIndex));
            }
        }

        var weaponServices = pawn.WeaponServices;
        if (weaponServices == null)
        {
            return false;
        }

        weaponServices.ActiveWeapon.Raw = weapon.EntityHandle.Raw;
        Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pWeaponServices");

        if (player.UserId != null)
        {
            NativeAPI.IssueClientCommand(player.UserId.Value, $"use {weapon.DesignerName}");
        }

        return true;
    }

    private static IEnumerable<CBasePlayerWeapon> GetWeaponsInReplaySlot(CCSPlayerPawn pawn, ReplayWeaponSlot slot)
    {
        if (pawn.WeaponServices == null)
        {
            yield break;
        }

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
            {
                continue;
            }

            if (GetReplayWeaponSlot(NormalizeLoadoutItem(weapon.DesignerName)) == slot)
            {
                yield return weapon;
            }
        }
    }

    private static void GiveMissingTargetItemsDirect(
        CCSPlayerController player,
        Dictionary<string, int> targetItems,
        Func<string, bool> predicate)
    {
        var currentItems = CountItems(GetCurrentInventory(player).Select(NormalizeLoadoutItem));
        foreach (var (itemName, targetCount) in targetItems.Where(pair => predicate(pair.Key)).ToList())
        {
            var missingCount = Math.Max(0, targetCount - currentItems.GetValueOrDefault(itemName));
            for (var i = 0; i < missingCount; i++)
            {
                player.GiveNamedItem(itemName);
            }
        }
    }

    private static int BuyTargetItems(
        CCSPlayerController player,
        Dictionary<string, int> targetItems,
        int currentMoney,
        int alreadySpent,
        Func<string, bool> predicate)
    {
        var spent = 0;
        foreach (var (itemName, targetCount) in targetItems.Where(pair => predicate(pair.Key)).ToList())
        {
            for (var i = 0; i < targetCount; i++)
            {
                if (!CanReplayBuyItem(player, currentMoney, alreadySpent + spent, itemName))
                {
                    break;
                }

                player.GiveNamedItem(itemName);
                if (!IsDefaultPistolForTeam(player.Team, itemName))
                {
                    spent += ItemPrice(itemName);
                }
            }
        }

        return spent;
    }

    private static void StripAllWeapons(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn?.WeaponServices == null) return;

        var toRemove = new List<CBasePlayerWeapon>();
        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var w = handle.Value;
            if (w == null || !w.IsValid) continue;
            var name = w.DesignerName;
            if (string.IsNullOrEmpty(name)) continue;
            if (name.Contains("knife")
                || name == "weapon_bayonet"
                || name == "weapon_c4"
                || name == "weapon_c4_explosive") continue;
            toRemove.Add(w);
        }

        foreach (var weapon in toRemove)
        {
            RemovePlayerWeaponAndCleanupDrop(player, weapon);
        }
    }

    private static void RemoveUnownedReplayWeapons()
    {
        foreach (var itemName in ItemPrices.Keys)
        {
            if (!itemName.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
                || itemName.Equals("weapon_knife", StringComparison.OrdinalIgnoreCase)
                || itemName.Equals("weapon_knife_t", StringComparison.OrdinalIgnoreCase)
                || itemName.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var weapon in Utilities.FindAllEntitiesByDesignerName<CBasePlayerWeapon>(itemName))
            {
                if (weapon == null || !weapon.IsValid || weapon.OwnerEntity.IsValid)
                {
                    continue;
                }

                weapon.AcceptInput("Kill");
            }
        }
    }

    private int ApplyTeamLoadouts(List<BotReplayAssignment> assignments)
    {
        var applied = 0;
        foreach (var assignment in assignments)
        {
            if (ApplyLoadout(assignment.Bot, assignment.Player, assignment.Budget))
            {
                _loadoutAppliedKeys.Add(PlayerKey(assignment.Bot));
                applied++;
            }
        }
        return applied;
    }

    private static void TransferSavedUtility(List<BotReplayAssignment> assignments)
    {
        var states = assignments
            .Select(assignment => new UtilityTransferState(
                assignment,
                CountItems(GetCurrentInventory(assignment.Bot)),
                CountItems(assignment.Player.Inventory.Where(IsGiveableItem))))
            .ToList();

        foreach (var itemName in ThrowableUtilityItems)
        {
            foreach (var receiver in states)
            {
                var missingCount = receiver.Missing(itemName);
                for (var itemIndex = 0; itemIndex < missingCount; itemIndex++)
                {
                    var donor = states.FirstOrDefault(candidate => !ReferenceEquals(candidate, receiver) && candidate.Surplus(itemName) > 0);
                    if (donor == null)
                    {
                        break;
                    }

                    RemoveInventoryItemsAndCleanupDrops(donor.Assignment.Bot, itemName, 1);
                    receiver.Assignment.Bot.GiveNamedItem(itemName);
                    donor.CurrentItems[itemName] = donor.CurrentItems.GetValueOrDefault(itemName) - 1;
                    receiver.CurrentItems[itemName] = receiver.CurrentItems.GetValueOrDefault(itemName) + 1;
                }
            }
        }
    }

    private void ApplyUtilityCap(ReplayPlayer replayPlayer, Dictionary<string, int> targetItems)
    {
        if (_config.MaxUtilityBeyondThrown < 0)
        {
            return;
        }

        var thrownCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var grenade in replayPlayer.Grenades)
        {
            var normalized = NormalizeGrenadeType(grenade.Type);
            if (string.IsNullOrEmpty(normalized) || !ThrowableUtilityItems.Contains(normalized))
            {
                continue;
            }
            thrownCounts[normalized] = thrownCounts.GetValueOrDefault(normalized) + 1;
        }

        foreach (var itemName in ThrowableUtilityItems)
        {
            if (!targetItems.TryGetValue(itemName, out var current))
            {
                continue;
            }

            var thrown = thrownCounts.GetValueOrDefault(itemName);
            var cap = thrown + _config.MaxUtilityBeyondThrown;
            if (current <= cap)
            {
                continue;
            }

            if (cap <= 0)
            {
                targetItems.Remove(itemName);
            }
            else
            {
                targetItems[itemName] = cap;
            }
        }
    }

    private static void PrepareSecondaryTarget(
        CCSPlayerController player,
        Dictionary<string, int> currentItems,
        Dictionary<string, int> targetItems,
        int currentMoney,
        int spent)
    {
        var targetSecondary = BestSecondary(targetItems.Keys);
        if (targetSecondary == null)
        {
            EnsureDefaultPistolTarget(player, currentItems, targetItems);
            return;
        }

        if (DefaultPistols.Contains(targetSecondary))
        {
            return;
        }

        if (!CanReplayBuyItem(player, currentMoney, spent, targetSecondary))
        {
            targetItems.Remove(targetSecondary);
            EnsureDefaultPistolTarget(player, currentItems, targetItems);
        }
    }

    private static void EnsureDefaultPistolTarget(
        CCSPlayerController player,
        Dictionary<string, int> currentItems,
        Dictionary<string, int> targetItems)
    {
        if (targetItems.Keys.Any(itemName => SecondaryWeapons.Contains(itemName)))
        {
            return;
        }

        var defaultPistol = CurrentDefaultPistol(player.Team, currentItems.Keys) ?? DefaultPistolForTeam(player.Team);
        if (defaultPistol == null)
        {
            return;
        }

        targetItems[defaultPistol] = Math.Max(1, targetItems.GetValueOrDefault(defaultPistol));
    }

    private static void EnsureDefaultPistol(CCSPlayerController player)
    {
        var currentItems = CountItems(GetCurrentInventory(player));
        if (currentItems.Keys.Any(itemName => SecondaryWeapons.Contains(itemName)))
        {
            return;
        }

        var defaultPistol = DefaultPistolForTeam(player.Team);
        if (defaultPistol != null)
        {
            player.GiveNamedItem(defaultPistol);
        }
    }

    private static void EnsurePrimaryOrSecondaryFallback(CCSPlayerController player)
    {
        if (!player.IsValid || !player.PawnIsAlive)
        {
            return;
        }

        var currentItems = CountItems(GetCurrentInventory(player).Select(NormalizeLoadoutItem));
        if (currentItems.Keys.Any(itemName => PrimaryWeapons.Contains(itemName) || SecondaryWeapons.Contains(itemName)))
        {
            return;
        }

        var defaultPistol = DefaultPistolForTeam(player.Team);
        if (defaultPistol != null)
        {
            TryGiveNamedItem(player, defaultPistol);
        }
    }

    private static bool ReplayActivelyUsesWeapon(ReplayPlayer replayPlayer, string itemName)
    {
        var defIndex = WeaponDefIndex(itemName);
        if (defIndex >= 0)
        {
            var normalized = NormalizeWeaponDefIndex(defIndex);
            if (replayPlayer.FirstWeaponDefIndex == normalized
                || replayPlayer.PreloadWeaponDefIndexes.Any(def => NormalizeWeaponDefIndex(def) == normalized)
                || replayPlayer.InventoryDefIndexes.Any(def => NormalizeWeaponDefIndex(def) == normalized))
            {
                return true;
            }
        }

        return replayPlayer.StartFrame?.ActiveWeapon.Equals(itemName, StringComparison.OrdinalIgnoreCase) == true
            || replayPlayer.EndFrame?.ActiveWeapon.Equals(itemName, StringComparison.OrdinalIgnoreCase) == true
            || replayPlayer.RetakeStartFrame?.ActiveWeapon.Equals(itemName, StringComparison.OrdinalIgnoreCase) == true
            || replayPlayer.RetakeEndFrame?.ActiveWeapon.Equals(itemName, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool CanReplayBuyItem(CCSPlayerController player, int currentMoney, int spent, string itemName)
    {
        var remainingMoney = Math.Max(0, currentMoney - spent);
        var price = ItemPrice(itemName);
        var isTerrorist = player.Team == CsTeam.Terrorist;
        var isCounterTerrorist = player.Team == CsTeam.CounterTerrorist;

        var canBuyOnTeam = itemName switch
        {
            "weapon_glock" => isTerrorist,
            "weapon_hkp2000" or "weapon_usp_silencer" => isCounterTerrorist,
            "weapon_tec9" or "weapon_mac10" or "weapon_sawedoff" or "weapon_galilar" or "weapon_ak47" or "weapon_sg556" or "weapon_g3sg1" or "weapon_molotov" => isTerrorist,
            "weapon_fiveseven" or "weapon_mp9" or "weapon_mag7" or "weapon_famas" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_aug" or "weapon_scar20" or "weapon_incgrenade" or "item_defuser" => isCounterTerrorist,
            _ => ItemPrices.ContainsKey(itemName)
        };

        return canBuyOnTeam && price <= remainingMoney;
    }

    private static void RemoveSurplusItems(
        CCSPlayerController player,
        Dictionary<string, int> currentItems,
        Dictionary<string, int> targetItems,
        string? preservedPrimary)
    {
        foreach (var (itemName, ownedCount) in currentItems.ToList())
        {
            if (preservedPrimary != null && itemName.Equals(preservedPrimary, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetCount = targetItems.GetValueOrDefault(itemName);
            var surplusCount = Math.Max(0, ownedCount - targetCount);
            RemoveInventoryItemsAndCleanupDrops(player, itemName, surplusCount);

            if (surplusCount == 0)
            {
                continue;
            }

            if (targetCount > 0)
            {
                currentItems[itemName] = targetCount;
            }
            else
            {
                currentItems.Remove(itemName);
            }
        }
    }

    private static int ApplyArmorAndKit(CCSPlayerController player, CCSPlayerPawn pawn, ReplayPlayer replayPlayer, int remainingMoney)
    {
        var spent = 0;
        var itemServices = pawn.ItemServices != null && pawn.ItemServices.Handle != IntPtr.Zero
            ? new CCSPlayer_ItemServices(pawn.ItemServices.Handle)
            : null;
        var boughtHelmetWithArmor = false;

        if (replayPlayer.ArmorValue > pawn.ArmorValue)
        {
            var armorPrice = replayPlayer.HasHelmet ? (pawn.ArmorValue > 0 ? 350 : 1_000) : 650;
            if (armorPrice > remainingMoney)
            {
                return spent;
            }

            spent += armorPrice;
            remainingMoney -= armorPrice;
            boughtHelmetWithArmor = replayPlayer.HasHelmet;
            pawn.ArmorValue = replayPlayer.ArmorValue;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        }

        if (itemServices != null && replayPlayer.HasHelmet && !itemServices.HasHelmet)
        {
            if (!boughtHelmetWithArmor)
            {
                var helmetPrice = pawn.ArmorValue > 0 ? 350 : 1_000;
                if (helmetPrice > remainingMoney)
                {
                    return spent;
                }

                spent += helmetPrice;
                remainingMoney -= helmetPrice;
            }
            itemServices.HasHelmet = true;
        }

        if (itemServices != null && player.Team == CsTeam.CounterTerrorist && replayPlayer.HasDefuser && !itemServices.HasDefuser)
        {
            var defuserPrice = ItemPrice("item_defuser");
            if (defuserPrice > remainingMoney)
            {
                return spent;
            }

            spent += defuserPrice;
            itemServices.HasDefuser = true;
        }

        return spent;
    }

    private static void RemoveReplayManagedWeapons(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn?.WeaponServices == null)
        {
            return;
        }

        var weapons = pawn.WeaponServices.MyWeapons
            .Select(handle => handle.Value)
            .Where(weapon => weapon != null && weapon.IsValid)
            .Select(weapon => weapon!)
            .Where(weapon => weapon.DesignerName != "weapon_knife"
                && weapon.DesignerName != "weapon_knife_t"
                && weapon.DesignerName != "weapon_c4")
            .ToList();

        foreach (var weapon in weapons)
        {
            RemovePlayerWeaponAndCleanupDrop(player, weapon);
        }
    }

}
