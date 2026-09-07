using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace BotRandomizer;

internal sealed class TeamIntroPreview
{
    private readonly CosmeticApplicator _applicator;
    private readonly Func<CCSPlayerController?, SlotCosmeticState?> _getOrCreateState;
    private readonly Func<BotCosmeticLoadout, ushort, WeaponCosmeticSelection?> _getOrCreateWeapon;
    private readonly ILogger _logger;
    // Xuid==0 slots have no Valve owner; keep bot pairing stable for the intro window.
    private readonly Dictionary<uint, int> _unassignedPairs = [];
    private readonly Dictionary<uint, PreviewPaintSignature> _painted = [];

    internal TeamIntroPreview(
        CosmeticApplicator applicator,
        Func<CCSPlayerController?, SlotCosmeticState?> getOrCreateState,
        Func<BotCosmeticLoadout, ushort, WeaponCosmeticSelection?> getOrCreateWeapon,
        ILogger logger)
    {
        _applicator = applicator;
        _getOrCreateState = getOrCreateState;
        _getOrCreateWeapon = getOrCreateWeapon;
        _logger = logger;
    }

    internal void Reset()
    {
        _unassignedPairs.Clear();
        _painted.Clear();
    }

    internal void ForgetSlot(int slot)
    {
        foreach (var index in _unassignedPairs
                     .Where(pair => pair.Value == slot)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _unassignedPairs.Remove(index);
            _painted.Remove(index);
        }
    }

    internal void Reconcile()
    {
        var gameRules = Utilities
            .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()
            ?.GameRules;
        if (gameRules is null || !gameRules.TeamIntroPeriod)
        {
            if (_unassignedPairs.Count > 0 || _painted.Count > 0)
                Reset();
            return;
        }

        ReconcileTeam(
            "team_intro_counterterrorist",
            CsTeam.CounterTerrorist,
            gameRules.CTTeamIntroVariant);
        ReconcileTeam(
            "team_intro_terrorist",
            CsTeam.Terrorist,
            gameRules.TTeamIntroVariant);
        ReconcileTeam(
            "wingman_intro_counterterrorist",
            CsTeam.CounterTerrorist,
            gameRules.CTTeamIntroVariant);
        ReconcileTeam(
            "wingman_intro_terrorist",
            CsTeam.Terrorist,
            gameRules.TTeamIntroVariant);
    }

    private void ReconcileTeam(string designerName, CsTeam team, int introVariant)
    {
        var teamPlayers = Utilities.GetPlayers()
            .Where(player => player.IsValid && (CsTeam)player.TeamNum == team)
            .OrderBy(player => player.Slot)
            .ToList();
        if (teamPlayers.Count == 0)
            return;

        var bots = teamPlayers
            .Where(player => player.IsBot && !player.IsHLTV)
            .ToList();
        var previews = Utilities
            .FindAllEntitiesByDesignerName<CCSGO_TeamPreviewCharacterPosition>(designerName)
            .Where(preview => preview.IsValid && preview.Variant == introVariant)
            .OrderBy(preview => preview.Ordinal)
            .Take(teamPlayers.Count)
            .ToList();
        if (previews.Count == 0)
            return;

        var claimedSlots = new HashSet<int>();
        var paintedThisPass = new HashSet<uint>();

        foreach (var preview in previews)
        {
            var xuid = preview.Xuid;
            if (xuid == 0)
                continue;

            _unassignedPairs.Remove(preview.Index);
            var owner = Utilities.GetPlayers()
                .FirstOrDefault(player => player.IsValid && player.SteamID == xuid);
            if (owner is null || !owner.IsBot || owner.IsHLTV)
                continue;

            if (ApplyPreview(preview, owner))
            {
                claimedSlots.Add(owner.Slot);
                paintedThisPass.Add(preview.Index);
            }
        }

        var unmatchedBots = bots
            .Where(bot => !claimedSlots.Contains(bot.Slot))
            .OrderBy(bot => bot.Slot)
            .ToList();
        var emptyPreviews = previews
            .Where(preview => preview.Xuid == 0)
            .OrderBy(preview => preview.Ordinal)
            .ToList();

        foreach (var preview in emptyPreviews)
        {
            if (!_unassignedPairs.TryGetValue(preview.Index, out var pairedSlot))
                continue;
            var bot = unmatchedBots.FirstOrDefault(candidate => candidate.Slot == pairedSlot);
            if (bot is null)
            {
                _unassignedPairs.Remove(preview.Index);
                continue;
            }

            if (ApplyPreview(preview, bot))
            {
                unmatchedBots.Remove(bot);
                paintedThisPass.Add(preview.Index);
            }
        }

        foreach (var preview in emptyPreviews)
        {
            if (paintedThisPass.Contains(preview.Index))
                continue;
            if (unmatchedBots.Count == 0)
                break;

            var bot = unmatchedBots[0];
            unmatchedBots.RemoveAt(0);
            _unassignedPairs[preview.Index] = bot.Slot;
            if (ApplyPreview(preview, bot))
                paintedThisPass.Add(preview.Index);
        }
    }

    private bool ApplyPreview(CCSGO_TeamPreviewCharacterPosition preview, CCSPlayerController bot)
    {
        var state = _getOrCreateState(bot);
        if (state is null)
            return false;

        var loadout = state.Loadout;
        var weaponName = preview.WeaponName;
        var isKnife = IsKnifePreview(weaponName);
        ushort weaponDef = 0;
        int weaponPaint = 0;
        int weaponSeed = 0;
        float weaponWear = 0.01f;
        if (isKnife)
        {
            weaponDef = loadout.Knife.DefIndex;
            weaponPaint = loadout.Knife.PaintKit;
            weaponWear = loadout.Knife.Wear;
        }
        else if (!string.IsNullOrWhiteSpace(weaponName))
        {
            var designerName = weaponName.StartsWith("weapon_", StringComparison.Ordinal)
                ? weaponName
                : $"weapon_{weaponName}";
            if (RandomizerAssets.KnifeDefIndexByName.ContainsKey(designerName))
            {
                weaponDef = loadout.Knife.DefIndex;
                weaponPaint = loadout.Knife.PaintKit;
                weaponWear = loadout.Knife.Wear;
                isKnife = true;
            }
            else
            {
                var itemDef = preview.WeaponItem.Handle != IntPtr.Zero
                    ? preview.WeaponItem.ItemDefinitionIndex
                    : (ushort)0;
                var selection = itemDef != 0
                    ? _getOrCreateWeapon(loadout, itemDef)
                    : null;
                if (selection is not null)
                {
                    weaponDef = itemDef;
                    weaponPaint = selection.PaintKit;
                    weaponSeed = selection.Seed;
                    weaponWear = selection.Wear;
                }
            }
        }

        var signature = new PreviewPaintSignature(
            preview.Xuid,
            bot.Slot,
            loadout.AgentDefIndex,
            loadout.Knife.DefIndex,
            loadout.Knife.PaintKit,
            loadout.Glove.DefIndex,
            loadout.Glove.PaintKit,
            weaponDef,
            weaponPaint);
        if (_painted.TryGetValue(preview.Index, out var previous) && previous == signature)
            return true;

        try
        {
            // Do not write m_xuid. Valve already owns the slot assignment.
            if (loadout.AgentDefIndex != 0 && preview.AgentItem.Handle != IntPtr.Zero)
            {
                preview.AgentItem.ItemDefinitionIndex = loadout.AgentDefIndex;
                Utilities.SetStateChanged(preview, "CCSGO_TeamPreviewCharacterPosition", "m_agentItem");
            }

            if (_applicator.TryPaintPreviewItem(
                    preview.GlovesItem,
                    loadout.Glove.DefIndex,
                    loadout.Glove.PaintKit,
                    0,
                    loadout.Glove.Wear,
                    entityQuality: 3,
                    bot.SteamID))
            {
                Utilities.SetStateChanged(preview, "CCSGO_TeamPreviewCharacterPosition", "m_glovesItem");
            }

            if (weaponDef != 0
                && _applicator.TryPaintPreviewItem(
                    preview.WeaponItem,
                    weaponDef,
                    weaponPaint,
                    weaponSeed,
                    weaponWear,
                    entityQuality: isKnife ? 3 : 0,
                    bot.SteamID))
            {
                Utilities.SetStateChanged(preview, "CCSGO_TeamPreviewCharacterPosition", "m_weaponItem");
            }

            _painted[preview.Index] = signature;
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "[BotRandomizer] Failed to apply team intro cosmetics");
            return false;
        }
    }

    private static bool IsKnifePreview(string? weaponName)
    {
        if (string.IsNullOrWhiteSpace(weaponName))
            return true;
        return weaponName.Contains("knife", StringComparison.OrdinalIgnoreCase)
            || weaponName.Contains("bayonet", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct PreviewPaintSignature(
        ulong Xuid,
        int BotSlot,
        ushort AgentDef,
        ushort KnifeDef,
        int KnifePaint,
        ushort GloveDef,
        int GlovePaint,
        ushort WeaponDef,
        int WeaponPaint);
}
