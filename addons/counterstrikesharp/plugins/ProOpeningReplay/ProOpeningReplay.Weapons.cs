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
    private static int WeaponDefIndex(string activeWeapon)
    {
        var itemName = NormalizeGrenadeType(activeWeapon);
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return -1;
        }

        if (WeaponDefIndexes.TryGetValue(itemName, out var defIndex))
        {
            return defIndex;
        }

        return itemName.Contains("knife", StringComparison.OrdinalIgnoreCase)
            || itemName.Contains("bayonet", StringComparison.OrdinalIgnoreCase)
            ? 42
            : -1;
    }

    private static int WeaponDefIndex(ReplayFrame frame)
    {
        if (frame.ActiveWeaponDefIndex.HasValue)
        {
            return NormalizeWeaponDefIndex(frame.ActiveWeaponDefIndex.Value);
        }
        return WeaponDefIndex(frame.ActiveWeapon);
    }

    private bool PreloadReplayWeapons(
        CCSPlayerController player,
        ReplayPlayer replayPlayer,
        List<ReplayFrame> frames,
        ReplaySessionKind kind,
        bool allowInventoryMutation)
    {
        var slot = player.Slot;
        if (slot < 0)
        {
            return false;
        }

        if (!allowInventoryMutation)
        {
            return false;
        }

        var weaponDefs = kind == ReplaySessionKind.Retake
            ? ReplayWeaponDefs(replayPlayer, frames)
            : ReplayInitialLoadoutWeaponDefs(replayPlayer);

        foreach (var defIndex in weaponDefs)
        {
            var normalized = NormalizeWeaponDefIndex(defIndex);
            if (!ShouldApplyReplayWeaponForSession(kind, normalized)
                || !IsPreloadWeaponDefIndex(normalized)
                || !_preloadedReplayWeapons.Add((slot, normalized)))
            {
                continue;
            }

            EnsureReplayWeaponForSlot(
                slot,
                normalized,
                forceSwitch: false,
                allowGive: allowInventoryMutation,
                replaceConflictingSlot: kind == ReplaySessionKind.Opening && IsSlotReplaceableWeaponDef(normalized));
        }

        return true;
    }

    private static IEnumerable<int> ReplayWeaponDefs(ReplaySession session)
        => ReplayWeaponDefs(session.ReplayPlayer, session.Frames);

    private static IEnumerable<int> ReplayWeaponDefs(ReplayPlayer replayPlayer, List<ReplayFrame> frames)
    {
        foreach (var defIndex in ReplayPlayerWeaponDefs(replayPlayer))
        {
            yield return defIndex;
        }

        foreach (var frame in frames)
        {
            yield return WeaponDefIndex(frame);
        }
    }

    private static int ChooseStartWeaponDef(ReplaySession session)
    {
        if (session.Kind == ReplaySessionKind.Retake)
        {
            if (session.Frames.Count == 0)
            {
                return -1;
            }

            var firstFrameDef = NormalizeWeaponDefIndex(WeaponDefIndex(session.Frames[0]));
            return ShouldApplyReplayWeaponForSession(session, firstFrameDef) ? firstFrameDef : -1;
        }

        var first = NormalizeWeaponDefIndex(session.ReplayPlayer.FirstWeaponDefIndex);
        if (IsKnownWeaponDefIndex(first) && GetReplayLockTarget(first) != LockTarget.Slot5)
        {
            return first;
        }

        foreach (var frame in session.Frames)
        {
            var defIndex = WeaponDefIndex(frame);
            if (IsKnownWeaponDefIndex(defIndex) && GetReplayLockTarget(defIndex) != LockTarget.Slot5)
            {
                return NormalizeWeaponDefIndex(defIndex);
            }
        }

        foreach (var defIndex in ReplayWeaponDefs(session))
        {
            var normalized = NormalizeWeaponDefIndex(defIndex);
            if (IsKnownWeaponDefIndex(normalized))
            {
                return normalized;
            }
        }

        return -1;
    }

    private bool ApplyReplayWeaponPreset(
        ReplaySession session,
        int weaponDefIndex,
        bool allowSlotReplacement,
        bool force)
    {
        var slot = session.Player.Slot;
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (slot < 0 || !IsKnownWeaponDefIndex(normalized))
        {
            return false;
        }

        if (!ShouldApplyReplayWeaponForSession(session, normalized))
        {
            return false;
        }

        if (!force
            && _lastReplayWeaponDef.TryGetValue(slot, out var lastDef)
            && lastDef == normalized
            && IsReplayWeaponActive(session.Player, normalized))
        {
            return true;
        }

        var target = GetReplayLockTarget(normalized);
        if (target != LockTarget.None
            && (force
                || !_lastLockedWeaponTarget.TryGetValue(slot, out var lastTarget)
                || lastTarget != target))
        {
            if (BotController.Lock(slot, target))
            {
                _lastLockedWeaponTarget[slot] = target;
            }
        }

        if (allowSlotReplacement)
        {
            EnsureReplayWeaponForSlot(
                slot,
                normalized,
                forceSwitch: false,
                allowGive: true,
                replaceConflictingSlot: IsSlotReplaceableWeaponDef(normalized));
        }

        var switched = BotController.SwitchBotWeapon(slot, NativeWeaponDefIndex(normalized));
        if (switched || IsReplayWeaponActive(session.Player, normalized))
        {
            _lastReplayWeaponDef[slot] = normalized;
            return true;
        }

        return false;
    }

    private static bool IsReplayWeaponActive(CCSPlayerController player, int weaponDefIndex)
    {
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (!IsKnownWeaponDefIndex(normalized))
        {
            return false;
        }

        var nativeDef = player.Slot >= 0 ? BotController.GetBotActiveWeaponDef(player.Slot) : -1;
        if (nativeDef >= 0)
        {
            return NormalizeWeaponDefIndex(nativeDef) == normalized;
        }

        var active = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        return active != null
            && active.IsValid
            && NormalizeWeaponDefIndex(WeaponDefIndex(active.DesignerName)) == normalized;
    }

    private void EnsureReplayWeaponForSlot(
        int slot,
        int weaponDefIndex,
        bool forceSwitch,
        bool allowGive,
        bool replaceConflictingSlot)
    {
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (normalized < 0)
        {
            return;
        }

        if (_lastEnsuredWeaponDef.TryGetValue(slot, out var last)
            && last == normalized
            && !forceSwitch)
        {
            return;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true }
            || player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
        {
            return;
        }

        if (!TryEnsureReplayWeapon(
                player,
                normalized,
                allowGive,
                replaceConflictingSlot,
                out _))
        {
            _lastEnsuredWeaponDef.Remove(slot);
            return;
        }

        _lastEnsuredWeaponDef[slot] = normalized;
        if (forceSwitch)
        {
            BotController.SwitchBotWeapon(slot, NativeWeaponDefIndex(normalized));
        }
    }

    private static IEnumerable<int> ReplayPlayerWeaponDefs(ReplayPlayer replayPlayer)
    {
        foreach (var defIndex in replayPlayer.InventoryDefIndexes)
        {
            yield return defIndex;
        }

        foreach (var defIndex in replayPlayer.PreloadWeaponDefIndexes)
        {
            yield return defIndex;
        }

        yield return replayPlayer.FirstWeaponDefIndex;

        foreach (var item in replayPlayer.Inventory)
        {
            yield return WeaponDefIndex(item);
        }
    }

    private static IEnumerable<int> ReplayInitialLoadoutWeaponDefs(ReplayPlayer replayPlayer)
    {
        foreach (var defIndex in replayPlayer.InventoryDefIndexes)
        {
            yield return defIndex;
        }

        yield return replayPlayer.FirstWeaponDefIndex;

        foreach (var item in replayPlayer.Inventory)
        {
            yield return WeaponDefIndex(item);
        }
    }

    private static bool TryEnsureReplayWeapon(
        CCSPlayerController player,
        int weaponDefIndex,
        bool allowGive,
        bool replaceConflictingSlot,
        out string className)
    {
        className = string.Empty;
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out className))
        {
            return false;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true })
        {
            return false;
        }

        if (HasReplayWeapon(pawn, className))
        {
            return true;
        }

        var slot = GetReplayWeaponSlot(className);
        if (!allowGive
            || slot is ReplayWeaponSlot.Other or ReplayWeaponSlot.Knife or ReplayWeaponSlot.C4 or ReplayWeaponSlot.Taser)
        {
            return false;
        }

        if (!replaceConflictingSlot && HasConflictingWeaponInSlot(pawn, slot, className))
        {
            return false;
        }

        if (replaceConflictingSlot)
        {
            RemoveConflictingReplaySlotWeapons(player, pawn, slot, className);
            if (HasReplayWeapon(pawn, className))
            {
                return true;
            }
            if (HasConflictingWeaponInSlot(pawn, slot, className))
            {
                return false;
            }
        }

        try
        {
            player.GiveNamedItem(className);
        }
        catch (Exception ex)
        {
            _ = ex;
            return false;
        }

        return HasReplayWeapon(pawn, className) || slot == ReplayWeaponSlot.Utility;
    }

    private static void RemoveConflictingReplaySlotWeapons(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayWeaponSlot slot,
        string expectedClassName)
    {
        if (slot is not (ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary) || pawn.WeaponServices == null)
        {
            return;
        }

        var toRemove = pawn.WeaponServices.MyWeapons
            .Select(handle => handle.Value)
            .Where(weapon => weapon != null && weapon.IsValid)
            .Select(weapon => weapon!)
            .Where(weapon => !WeaponClassMatches(weapon.DesignerName, expectedClassName))
            .Where(weapon => GetReplayWeaponSlot(weapon.DesignerName) == slot)
            .ToList();

        foreach (var weapon in toRemove)
        {
            DropAndKillReplayWeapon(player, pawn, weapon, "conflicting_slot");
        }
    }

    private static void RemoveInventoryItemsAndCleanupDrops(CCSPlayerController player, string itemName, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn?.WeaponServices == null)
        {
            for (var i = 0; i < count; i++)
            {
                player.RemoveItemByDesignerName(itemName);
            }
            ScheduleDroppedWeaponCleanup(itemName, 0, SnapshotPlayerOrigin(player));
            return;
        }

        var toRemove = pawn.WeaponServices.MyWeapons
            .Select(handle => handle.Value)
            .Where(weapon => weapon != null && weapon.IsValid)
            .Select(weapon => weapon!)
            .Where(weapon => NormalizeLoadoutItem(weapon.DesignerName).Equals(itemName, StringComparison.OrdinalIgnoreCase))
            .Take(count)
            .ToList();

        foreach (var weapon in toRemove)
        {
            RemovePlayerWeaponAndCleanupDrop(player, weapon);
        }

        for (var i = toRemove.Count; i < count; i++)
        {
            player.RemoveItemByDesignerName(itemName);
        }
        if (toRemove.Count < count)
        {
            ScheduleDroppedWeaponCleanup(itemName, 0, SnapshotPlayerOrigin(player));
        }
    }

    private static void RemovePlayerWeaponAndCleanupDrop(CCSPlayerController player, CBasePlayerWeapon weapon)
    {
        if (weapon == null || !weapon.IsValid)
        {
            return;
        }

        var weaponName = weapon.DesignerName;
        if (string.IsNullOrWhiteSpace(weaponName))
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn != null
            && pawn.IsValid
            && pawn.WeaponServices != null
            && DropAndKillReplayWeapon(player, pawn, weapon, "remove_inventory"))
        {
            return;
        }

        var weaponRaw = weapon.EntityHandle.Raw;
        var origin = SnapshotOrigin(weapon) ?? SnapshotPlayerOrigin(player);
        player.RemoveItemByDesignerName(weaponName);
        ScheduleDroppedWeaponCleanup(weaponName, weaponRaw, origin);
    }

    private static bool DropAndKillReplayWeapon(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CBasePlayerWeapon weapon,
        string reason)
    {
        var weaponName = weapon.DesignerName;
        if (!TrySelectWeapon(player, pawn, weapon))
        {
            return false;
        }

        try
        {
            player.DropActiveWeapon();
        }
        catch
        {
            return false;
        }

        KillDroppedWeapon(player.Slot, weapon, weaponName, reason);
        Server.NextFrame(() => KillDroppedWeapon(player.Slot, weapon, weaponName, reason));
        return true;
    }

    private static void KillDroppedWeapon(
        int slot,
        CBasePlayerWeapon weapon,
        string weaponName,
        string reason)
    {
        _ = slot;
        _ = weaponName;
        _ = reason;
        try
        {
            if (weapon.IsValid)
            {
                weapon.AcceptInput("Kill");
            }
        }
        catch
        {
            // Entity may already have been removed by the engine between frames.
        }
    }

    private static Vector? SnapshotOrigin(CBaseEntity entity)
    {
        var origin = entity.AbsOrigin;
        return origin == null ? null : new Vector(origin.X, origin.Y, origin.Z);
    }

    private static Vector? SnapshotPlayerOrigin(CCSPlayerController player)
    {
        var origin = player.PlayerPawn.Value?.AbsOrigin;
        return origin == null ? null : new Vector(origin.X, origin.Y, origin.Z);
    }

    private static void ScheduleDroppedWeaponCleanup(string weaponName, uint weaponRaw, Vector? origin)
        => ScheduleDroppedWeaponCleanup(weaponName, weaponRaw, origin, 6);

    private static void ScheduleDroppedWeaponCleanup(string weaponName, uint weaponRaw, Vector? origin, int framesRemaining)
    {
        Server.NextFrame(() =>
        {
            CleanupDroppedWeapon(weaponName, weaponRaw, origin);
            if (framesRemaining > 0)
            {
                ScheduleDroppedWeaponCleanup(weaponName, weaponRaw, origin, framesRemaining - 1);
            }
        });
    }

    private static void CleanupDroppedWeapon(string weaponName, uint weaponRaw, Vector? origin)
    {
        var killedByHandle = false;
        foreach (var weapon in Utilities.FindAllEntitiesByDesignerName<CBasePlayerWeapon>(weaponName))
        {
            if (weapon == null || !weapon.IsValid || weapon.OwnerEntity.IsValid)
            {
                continue;
            }

            if (weaponRaw != 0 && weapon.EntityHandle.Raw == weaponRaw)
            {
                weapon.AcceptInput("Kill");
                killedByHandle = true;
            }
        }

        if (killedByHandle || origin == null)
        {
            return;
        }

        const float cleanupRadius = 160.0f;
        var cleanupRadiusSq = cleanupRadius * cleanupRadius;
        foreach (var weapon in Utilities.FindAllEntitiesByDesignerName<CBasePlayerWeapon>(weaponName))
        {
            if (weapon == null || !weapon.IsValid || weapon.OwnerEntity.IsValid || weapon.AbsOrigin == null)
            {
                continue;
            }

            if (DistanceSquared(weapon.AbsOrigin, origin) <= cleanupRadiusSq)
            {
                weapon.AcceptInput("Kill");
            }
        }
    }

    private static bool HasReplayWeapon(CCSPlayerPawn pawn, string className)
    {
        if (pawn.WeaponServices == null)
        {
            return false;
        }

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
            {
                continue;
            }
            if (WeaponClassMatches(weapon.DesignerName, className))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasConflictingWeaponInSlot(CCSPlayerPawn pawn, ReplayWeaponSlot slot, string expectedClassName)
    {
        if (slot is not (ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary) || pawn.WeaponServices == null)
        {
            return false;
        }

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
            {
                continue;
            }
            if (WeaponClassMatches(weapon.DesignerName, expectedClassName))
            {
                continue;
            }
            if (GetReplayWeaponSlot(weapon.DesignerName) == slot)
            {
                return true;
            }
        }

        return false;
    }

    private static bool WeaponClassMatches(string actual, string expected)
    {
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return expected.Equals("weapon_knife", StringComparison.OrdinalIgnoreCase)
            && (actual.StartsWith("weapon_knife", StringComparison.OrdinalIgnoreCase)
                || actual.Contains("bayonet", StringComparison.OrdinalIgnoreCase));
    }

    private static ReplayWeaponSlot GetReplayWeaponSlot(string className)
    {
        if (className.StartsWith("weapon_knife", StringComparison.OrdinalIgnoreCase)
            || className.Contains("bayonet", StringComparison.OrdinalIgnoreCase))
        {
            return ReplayWeaponSlot.Knife;
        }

        return className switch
        {
            "weapon_ak47" or "weapon_aug" or "weapon_awp" or "weapon_famas" or
            "weapon_g3sg1" or "weapon_galilar" or "weapon_m249" or "weapon_m4a1" or
            "weapon_m4a1_silencer" or "weapon_mac10" or "weapon_p90" or
            "weapon_mp5sd" or "weapon_mp7" or "weapon_mp9" or "weapon_ump45" or
            "weapon_xm1014" or "weapon_bizon" or "weapon_mag7" or "weapon_negev" or
            "weapon_sawedoff" or "weapon_nova" or "weapon_scar20" or "weapon_sg556" or
            "weapon_ssg08" => ReplayWeaponSlot.Primary,

            "weapon_deagle" or "weapon_elite" or "weapon_fiveseven" or "weapon_glock" or
            "weapon_hkp2000" or "weapon_p250" or "weapon_tec9" or "weapon_usp_silencer" or
            "weapon_cz75a" or "weapon_revolver" => ReplayWeaponSlot.Secondary,

            "weapon_flashbang" or "weapon_hegrenade" or "weapon_smokegrenade" or
            "weapon_molotov" or "weapon_decoy" or "weapon_incgrenade" => ReplayWeaponSlot.Utility,

            "weapon_c4" => ReplayWeaponSlot.C4,
            "weapon_taser" => ReplayWeaponSlot.Taser,
            _ => ReplayWeaponSlot.Other
        };
    }

    private static LockTarget GetReplayLockTarget(int weaponDefIndex)
    {
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
        {
            return LockTarget.None;
        }

        return GetReplayWeaponSlot(className) switch
        {
            ReplayWeaponSlot.Primary => LockTarget.Slot1,
            ReplayWeaponSlot.Secondary => LockTarget.Slot2,
            ReplayWeaponSlot.Knife or ReplayWeaponSlot.Taser => LockTarget.Slot3,
            ReplayWeaponSlot.Utility => LockTarget.Slot4,
            ReplayWeaponSlot.C4 => LockTarget.Slot5,
            _ => LockTarget.None
        };
    }

    private static bool IsSlotReplaceableWeaponDef(int weaponDefIndex)
    {
        return TryGetWeaponClassByDefIndex(weaponDefIndex, out var className)
            && GetReplayWeaponSlot(className) is ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary;
    }

    private static bool IsPrimaryWeapon(string itemName)
        => PrimaryWeapons.Contains(itemName);

    private static bool IsSecondaryWeapon(string itemName)
        => SecondaryWeapons.Contains(itemName);

    private static bool IsPreloadWeaponDefIndex(int weaponDefIndex)
    {
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
        {
            return false;
        }

        return GetReplayWeaponSlot(className) is not ReplayWeaponSlot.Other
            and not ReplayWeaponSlot.Knife
            and not ReplayWeaponSlot.C4
            and not ReplayWeaponSlot.Taser;
    }

    private static bool ShouldApplyReplayWeaponForSession(ReplaySession session, int weaponDefIndex)
        => ShouldApplyReplayWeaponForSession(session.Kind, weaponDefIndex);

    private static bool ShouldApplyReplayWeaponForSession(ReplaySessionKind kind, int weaponDefIndex)
    {
        if (kind != ReplaySessionKind.Retake)
        {
            return true;
        }

        return TryGetWeaponClassByDefIndex(weaponDefIndex, out var className)
            && GetReplayWeaponSlot(className) == ReplayWeaponSlot.Utility;
    }

    private static bool IsKnownWeaponDefIndex(int weaponDefIndex)
        => TryGetWeaponClassByDefIndex(weaponDefIndex, out _);

    private static int NormalizeWeaponDefIndex(int weaponDefIndex)
    {
        if (weaponDefIndex == 42 || weaponDefIndex == 59 || weaponDefIndex is >= 500 and < 600 || weaponDefIndex == 9001)
        {
            return 42;
        }

        return weaponDefIndex;
    }

    private static int NativeWeaponDefIndex(int weaponDefIndex)
    {
        return NormalizeWeaponDefIndex(weaponDefIndex) == 42
            ? BotController.KnifeDef
            : weaponDefIndex;
    }

    private static bool TryGetWeaponClassByDefIndex(int weaponDefIndex, out string className)
    {
        className = NormalizeWeaponDefIndex(weaponDefIndex) switch
        {
            1 => "weapon_deagle",
            2 => "weapon_elite",
            3 => "weapon_fiveseven",
            4 => "weapon_glock",
            7 => "weapon_ak47",
            8 => "weapon_aug",
            9 => "weapon_awp",
            10 => "weapon_famas",
            11 => "weapon_g3sg1",
            13 => "weapon_galilar",
            14 => "weapon_m249",
            16 => "weapon_m4a1",
            17 => "weapon_mac10",
            19 => "weapon_p90",
            23 => "weapon_mp5sd",
            24 => "weapon_ump45",
            25 => "weapon_xm1014",
            26 => "weapon_bizon",
            27 => "weapon_mag7",
            28 => "weapon_negev",
            29 => "weapon_sawedoff",
            30 => "weapon_tec9",
            31 => "weapon_taser",
            32 => "weapon_hkp2000",
            33 => "weapon_mp7",
            34 => "weapon_mp9",
            35 => "weapon_nova",
            36 => "weapon_p250",
            38 => "weapon_scar20",
            39 => "weapon_sg556",
            40 => "weapon_ssg08",
            42 => "weapon_knife",
            43 => "weapon_flashbang",
            44 => "weapon_hegrenade",
            45 => "weapon_smokegrenade",
            46 => "weapon_molotov",
            47 => "weapon_decoy",
            48 => "weapon_incgrenade",
            49 => "weapon_c4",
            60 => "weapon_m4a1_silencer",
            61 => "weapon_usp_silencer",
            63 => "weapon_cz75a",
            64 => "weapon_revolver",
            _ => string.Empty
        };
        return className.Length > 0;
    }

}
