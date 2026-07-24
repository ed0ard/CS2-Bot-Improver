using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace PlayerKnifeCustomizer;

public readonly record struct StickerAttribute(string Name, float Value);

public static class StickerFailurePolicy
{
    public static bool ShouldRestoreBaseSkin(bool stickerApplySucceeded) => !stickerApplySucceeded;
}

public static class StickerAttributePlanner
{
    public static float EncodeUInt32(uint value) => BitConverter.Int32BitsToSingle(unchecked((int)value));

    public static bool TryBuild(
        ushort defIndex,
        bool enabled,
        IEnumerable<StickerPreset>? stickers,
        IReadOnlySet<uint> validIds,
        IReadOnlySet<ushort> validWeaponIds,
        out List<StickerAttribute> attributes,
        out string error)
    {
        attributes = new List<StickerAttribute>();
        error = string.Empty;
        var ordered = (stickers ?? []).OrderBy(sticker => sticker.Slot).ToArray();
        if (ordered.Length == 0) return true;
        if (defIndex is >= 500 and <= 526)
            return Fail("knife stickers are not supported", out error);
        if (ordered.Length > 5)
            return Fail("a weapon cannot have more than five stickers", out error);
        if (!validWeaponIds.Contains(defIndex))
            return Fail("stickers are not supported for this weapon", out error);
        if (!enabled) return true;

        var slots = new HashSet<byte>();
        foreach (var sticker in ordered)
        {
            if (sticker.Slot > 4 || !slots.Add(sticker.Slot))
                return Fail("sticker slots must be unique and between 0 and 4", out error);
            if (sticker.Id == 0 || !validIds.Contains(sticker.Id))
                return Fail("unknown sticker id", out error);
            if (!float.IsFinite(sticker.Wear) || sticker.Wear is < 0f or > 1f ||
                !float.IsFinite(sticker.Scale) || sticker.Scale is < 0.1f or > 2f ||
                !float.IsFinite(sticker.Rotation) || sticker.Rotation is < 0f or > 360f ||
                !float.IsFinite(sticker.OffsetX) || sticker.OffsetX is < -1f or > 1f ||
                !float.IsFinite(sticker.OffsetY) || sticker.OffsetY is < -1f or > 1f)
                return Fail("sticker values are outside the supported range", out error);

            string prefix = $"sticker slot {sticker.Slot}";
            attributes.Add(new StickerAttribute($"{prefix} id", EncodeUInt32(sticker.Id)));
            if (sticker.CustomPosition)
            {
                attributes.Add(new StickerAttribute($"{prefix} schema", 0f));
                attributes.Add(new StickerAttribute($"{prefix} offset x", sticker.OffsetX));
                attributes.Add(new StickerAttribute($"{prefix} offset y", sticker.OffsetY));
            }
            attributes.Add(new StickerAttribute($"{prefix} wear", sticker.Wear));
            attributes.Add(new StickerAttribute($"{prefix} scale", sticker.Scale));
            attributes.Add(new StickerAttribute($"{prefix} rotation", sticker.Rotation));
        }
        return true;

        static bool Fail(string message, out string target)
        {
            target = message;
            return false;
        }
    }
}

public sealed class PlayerKnifeCustomizerPlugin : BasePlugin
{
    private static readonly bool StickerReleaseEnabled = false;

    public override string ModuleName => "PlayerCosmetics";
    public override string ModuleVersion => "0.4.1";
    public override string ModuleAuthor => "CS2BotImproverPlus contributors";
    public override string ModuleDescription => "Applies Panel-defined player cosmetic presets";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly Dictionary<ushort, HashSet<int>> _validPaints = new();
    private readonly HashSet<uint> _validStickers = new();
    private readonly HashSet<ushort> _validStickerWeapons = new();
    private readonly Dictionary<ushort, List<WeaponSkinEntry>> _skinCatalog = new();
    private readonly HashSet<(ushort DefIndex, int Paint)> _legacyPaints = new();
    private KnifeConfig _config = new();
    private MemoryFunctionVoid<nint, string, float>? _setAttrByName;
    private ulong _nextItemId = 0xC5200000;
    private readonly ApplyErrorThrottle _applyErrorThrottle = new(TimeSpan.FromSeconds(30));
    private int _loadedConfigSchema = KnifeConfig.CurrentSchemaVersion;
    private int _loadedGunConfigSchema = KnifeConfig.CurrentSchemaVersion;
    private bool _stickersSanitizedDuringLoad;
    private readonly ApplyGenerationTracker _applyTracker = new();

    private string ConfigPath => Path.Combine(ModuleDirectory, "player_knife_presets.json");
    private string GunConfigPath => Path.Combine(ModuleDirectory, "player_gun_presets.json");
    private string CatalogPath => Path.Combine(ModuleDirectory, "weapon_skins.json");
    private string StickerCatalogPath => Path.Combine(ModuleDirectory, "sticker_ids.json");
    private string StickerWeaponCatalogPath => Path.Combine(ModuleDirectory, "sticker_weapon_ids.json");

    public override void Load(bool hotReload)
    {
        LoadCatalog();
        LoadStickerCatalog();
        LoadStickerWeaponCatalog();
        LoadConfig();

        AddCommand("css_cs2bi_knives_reload", "Reload player knife presets", OnReloadCommand);
        AddCommand("css_cs2bi_knives_status", "Show player knife preset status", OnStatusCommand);

        try
        {
            _setAttrByName = new MemoryFunctionVoid<nint, string, float>(
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? "55 48 89 E5 41 57 41 56 49 89 FE 41 55 41 54 53 48 89 F3 48 83 EC ? F3 0F 11 85"
                    : "40 53 55 41 56 48 81 EC 90 00 00 00");
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerKnifeCustomizer] Attribute signature unavailable: {Message}", ex.Message);
            _setAttrByName = null;
        }

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundMvp>(OnRoundMvp, HookMode.Pre);
        RegisterEventHandler<EventItemPickup>(OnItemPickup);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterListener<Listeners.OnMapStart>(_ => _applyTracker.CancelAll());
        RegisterListener<Listeners.OnMapEnd>(() => _applyTracker.CancelAll());
        VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveNamedItemPost, HookMode.Post);
        Logger.LogInformation("[PlayerKnifeCustomizer] Loaded generation-safe pipeline; enabled={Enabled}, signature={Signature}, catalog={Catalog}",
            _config.Enabled, _setAttrByName != null, _skinCatalog.Values.Sum(skins => skins.Count));
    }

    public override void Unload(bool hotReload)
    {
        _applyTracker.CancelAll();
        VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveNamedItemPost, HookMode.Post);
    }

    private HookResult OnGiveNamedItemPost(DynamicHook hook)
    {
        if (_setAttrByName == null || !_config.Enabled) return HookResult.Continue;
        try
        {
            var itemServices = hook.GetParam<CCSPlayer_ItemServices>(0);
            var player = GetPlayerFromItemServices(itemServices);
            if (!CanApplyToPlayer(player)) return HookResult.Continue;
            nint playerHandle = player!.Handle;
            long generation = _applyTracker.Begin(playerHandle, CosmeticApplyPhase.Guns);
            // The returned econ item is not safe for native attribute writes while this hook is active.
            ScheduleApplyCallbacks(playerHandle, generation);
        }
        catch (Exception ex)
        {
            LogApplyError("purchased weapon", ex);
        }
        return HookResult.Continue;
    }

    private static CCSPlayerController? GetPlayerFromItemServices(CCSPlayer_ItemServices itemServices)
    {
        var pawn = itemServices.Pawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.Controller.Value == null || !pawn.Controller.IsValid)
            return null;
        var player = new CCSPlayerController(pawn.Controller.Value.Handle);
        return player.IsValid ? player : null;
    }

    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo _)
    {
        var player = @event.Userid;
        if (!CanApplyToPlayer(player)) return HookResult.Continue;
        ScheduleApplyPipeline(player!.Handle, CosmeticApplyPhase.All);
        return HookResult.Continue;
    }

    public HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo _)
    {
        var player = @event.Userid;
        if (!CanApplyToPlayer(player) || _config.MusicKitId <= 0)
            return HookResult.Continue;

        ApplyMusicKit(player!);
        @event.Musickitid = _config.MusicKitId;
        @event.Nomusic = 0;
        return HookResult.Continue;
    }

    private void ApplyMusicKit(CCSPlayerController player)
    {
        if (!CanApplyToPlayer(player) || _config.MusicKitId <= 0)
            return;

        player.MusicKitID = _config.MusicKitId;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMusicKitID");
    }

    public HookResult OnItemPickup(EventItemPickup @event, GameEventInfo _)
    {
        var player = @event.Userid;
        if (!_config.ApplyOnPickup || !CanApplyToPlayer(player))
            return HookResult.Continue;

        ScheduleApplyPipeline(player!.Handle, CosmeticApplyPhase.Guns);
        return HookResult.Continue;
    }

    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo _)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (victim is { IsValid: true } && victim.Handle != nint.Zero)
            _applyTracker.Cancel(victim.Handle);
        if (!CanApplyToPlayer(attacker) || victim == null || !victim.IsValid || attacker == victim)
            return HookResult.Continue;

        var weapon = attacker!.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        if (weapon == null || !weapon.IsValid)
            return HookResult.Continue;

        var team = GetCosmeticTeam(attacker);
        if (team == null) return HookResult.Continue;
        ushort defIndex = weapon.AttributeManager?.Item?.ItemDefinitionIndex ?? 0;
        if (!TryGetPreset(defIndex, team.Value, out var preset) || !preset.StatTrakEnabled)
            return HookResult.Continue;

        preset.StatTrakCount++;
        ApplyStatTrak(weapon, preset);
        SaveConfig();
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo _)
    {
        var player = @event.Userid;
        if (CanApplyToPlayer(player))
            ScheduleApplyPipeline(player!.Handle, CosmeticApplyPhase.All);
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo _)
    {
        var player = @event.Userid;
        if (player != null && player.Handle != nint.Zero)
            _applyTracker.Cancel(player.Handle);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo _)
    {
        _applyTracker.CancelAll();
        return HookResult.Continue;
    }

    private void ScheduleApplyPipeline(nint playerHandle, CosmeticApplyPhase phases)
    {
        if (playerHandle == nint.Zero || phases == CosmeticApplyPhase.None) return;
        long generation = _applyTracker.Begin(playerHandle, phases);
        ScheduleApplyCallbacks(playerHandle, generation);
    }

    private void ScheduleApplyCallbacks(nint playerHandle, long generation)
    {
        Server.NextFrame(() => RunApplyPipeline(playerHandle, generation, false));
        for (int index = 0; index < ApplyPipelineContext.RetryDelays.Length; index++)
        {
            bool finalAttempt = index == ApplyPipelineContext.RetryDelays.Length - 1;
            AddTimer(ApplyPipelineContext.RetryDelays[index],
                () => RunApplyPipeline(playerHandle, generation, finalAttempt),
                TimerFlags.STOP_ON_MAPCHANGE);
        }
    }

    private void RunApplyPipeline(nint playerHandle, long generation, bool finalAttempt)
    {
        if (!_applyTracker.IsCurrent(playerHandle, generation) ||
            !_applyTracker.HasPending(playerHandle, generation))
            return;

        var player = ResolvePlayer(playerHandle);
        if (!CanApplyToPlayer(player))
        {
            if (finalAttempt) _applyTracker.MarkRetryExhausted(playerHandle, generation);
            return;
        }
        var team = GetCosmeticTeam(player);
        var pawn = player!.PlayerPawn.Value;
        if (!ApplyPipelineContext.IsReady(
                player.PawnIsAlive,
                pawn is { IsValid: true },
                pawn?.Handle ?? nint.Zero,
                team))
        {
            if (finalAttempt) _applyTracker.MarkRetryExhausted(playerHandle, generation);
            return;
        }
        var readyPawn = pawn!;
        var readyTeam = team!.Value;
        if (!_applyTracker.TryBindContext(playerHandle, generation, readyPawn.Handle, (int)readyTeam))
            return;

        TryApplyPhase(playerHandle, generation, CosmeticApplyPhase.Knife,
            () => TryApplyDefaultKnife(readyPawn, readyTeam), "knife pipeline");
        TryApplyPhase(playerHandle, generation, CosmeticApplyPhase.Gloves,
            () => TryApplyGlove(playerHandle, readyPawn, readyTeam), "glove pipeline");
        bool gunsPending = _applyTracker.IsPending(playerHandle, generation, CosmeticApplyPhase.Guns);
        TryApplyPhase(playerHandle, generation, CosmeticApplyPhase.Guns,
            () => TryApplyGunPresets(readyPawn, readyTeam), "gun pipeline");
        if (gunsPending && !_applyTracker.IsPending(playerHandle, generation, CosmeticApplyPhase.Guns))
            TryControlledReequip(player, readyPawn, readyTeam, generation);
        TryApplyPhase(playerHandle, generation, CosmeticApplyPhase.Music,
            () => { ApplyMusicKit(player); return true; }, "music pipeline");
        if (finalAttempt) _applyTracker.MarkRetryExhausted(playerHandle, generation);
    }

    private void TryApplyPhase(nint playerHandle, long generation, CosmeticApplyPhase phase,
        Func<bool> apply, string operation)
    {
        if (!_applyTracker.IsPending(playerHandle, generation, phase)) return;
        try
        {
            if (apply()) _applyTracker.Complete(playerHandle, generation, phase);
        }
        catch (Exception ex)
        {
            LogApplyError(operation, ex);
        }
    }

    private bool TryApplyDefaultKnife(CCSPlayerPawn pawn, CosmeticTeam team)
    {
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return false;
        var loadout = _config.Loadouts.For(team);

        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid || !IsKnifeName(weapon.DesignerName)) continue;
            var item = weapon.AttributeManager?.Item;
            if (item == null || !HasReadyAttributeLists(item)) return false;

            ushort target = loadout.DefaultKnifeDefIndex;
            if (target > 0 && loadout.KnifePresets.TryGetValue(target, out var targetPreset))
            {
                weapon.AcceptInput("ChangeSubclass", value: target.ToString());
                item.ItemDefinitionIndex = target;
                return ApplyPreset(weapon, target, targetPreset);
            }

            ushort current = item.ItemDefinitionIndex;
            return !TryGetPreset(current, team, out var currentPreset) ||
                   ApplyPreset(weapon, current, currentPreset);
        }

        return false;
    }

    private bool TryApplyGunPresets(CCSPlayerPawn pawn, CosmeticTeam team)
    {
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return false;
        bool ready = true;
        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid || IsKnifeName(weapon.DesignerName)) continue;
            ushort defIndex = weapon.AttributeManager?.Item?.ItemDefinitionIndex ?? 0;
            if (defIndex == 0 || !TryGetPreset(defIndex, team, out var preset)) continue;
            if (!ApplyPreset(weapon, defIndex, preset)) ready = false;
        }
        return ready;
    }

    private void TryControlledReequip(CCSPlayerController player, CCSPlayerPawn pawn, CosmeticTeam team, long generation)
    {
        if (!StickerReleaseEnabled || !_config.StickersEnabled) return;
        var active = pawn.WeaponServices?.ActiveWeapon.Value;
        if (active == null || !active.IsValid || IsKnifeName(active.DesignerName)) return;
        ushort defIndex = active.AttributeManager?.Item?.ItemDefinitionIndex ?? 0;
        if (!TryGetPreset(defIndex, team, out var preset) || (preset.Stickers?.Count ?? 0) == 0) return;
        if (!_applyTracker.TryMarkReequip(player.Handle, generation)) return;

        player.ExecuteClientCommand("lastinv");
        nint playerHandle = player.Handle;
        Server.NextFrame(() =>
        {
            var current = ResolvePlayer(playerHandle);
            if (CanApplyToPlayer(current)) current!.ExecuteClientCommand("lastinv");
        });
    }

    private bool TryGetPreset(ushort defIndex, CosmeticTeam team, out KnifePreset preset)
    {
        if (IsKnifeDefIndex(defIndex))
            return _config.Loadouts.For(team).KnifePresets.TryGetValue(defIndex, out preset!);
        return WeaponPresetResolver.TryResolveGunPreset(_config, defIndex, team, out preset!);
    }

    private bool ApplyPreset(CBasePlayerWeapon weapon, ushort defIndex, KnifePreset preset)
    {
        if (_setAttrByName == null || !weapon.IsValid || !ValidatePreset(defIndex, preset)) return false;

        try
        {
            var item = weapon.AttributeManager?.Item;
            if (item == null || !HasReadyAttributeLists(item)) return false;

            item.ItemDefinitionIndex = defIndex;
            item.EntityQuality = preset.SouvenirEnabled ? (byte)12
                : preset.StatTrakEnabled ? (byte)9 : (byte)3;
            item.CustomName = preset.NameTag ?? string.Empty;
            item.AttributeList.Attributes.RemoveAll();
            item.NetworkedDynamicAttributes.Attributes.RemoveAll();
            AssignItemId(item);

            weapon.FallbackPaintKit = preset.Paint;
            weapon.FallbackSeed = preset.Seed;
            weapon.FallbackWear = preset.Wear;

            SetTextureAttributes(item.NetworkedDynamicAttributes.Handle, preset);
            SetTextureAttributes(item.AttributeList.Handle, preset);
            if (preset.StatTrakEnabled && !preset.SouvenirEnabled) ApplyStatTrak(weapon, preset);
            if (StickerReleaseEnabled &&
                StickerFailurePolicy.ShouldRestoreBaseSkin(TryApplyStickers(defIndex, item, preset)))
                RestoreBaseAttributes(weapon, item, preset);

            Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
            bool legacyModel = _legacyPaints.Contains((defIndex, preset.Paint));
            weapon.AcceptInput("SetBodygroup", value: $"body,{(legacyModel ? 1 : 0)}");
            return true;
        }
        catch (Exception ex)
        {
            LogApplyError($"defindex {defIndex}", ex);
            return false;
        }
    }

    private bool TryApplyGlove(nint playerHandle, CCSPlayerPawn pawn, CosmeticTeam team)
    {
        var preset = _config.Loadouts.For(team).Glove;
        if (!preset.Enabled) return true;
        if (_setAttrByName == null || preset.DefIndex == 0 || preset.Paint <= 0) return false;

        try
        {
            var item = pawn.EconGloves;
            if (!HasReadyAttributeLists(item)) return false;
            item.NetworkedDynamicAttributes.Attributes.RemoveAll();
            item.AttributeList.Attributes.RemoveAll();
            item.ItemDefinitionIndex = preset.DefIndex;
            AssignItemId(item);

            SetTextureAttributes(item.NetworkedDynamicAttributes.Handle, preset.Paint, preset.Seed, preset.Wear);
            SetTextureAttributes(item.AttributeList.Handle, preset.Paint, preset.Seed, preset.Wear);
            item.Initialized = true;

            pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
            nint pawnHandle = pawn.Handle;
            AddTimer(0.20f, () =>
            {
                if (pawnHandle == nint.Zero) return;
                var player = ResolvePlayer(playerHandle);
                var currentPawn = player?.PlayerPawn.Value;
                if (player?.PawnIsAlive == true && currentPawn is { IsValid: true } && currentPawn.Handle == pawnHandle)
                    currentPawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
            }, TimerFlags.STOP_ON_MAPCHANGE);
            return true;
        }
        catch (Exception ex)
        {
            LogApplyError("gloves", ex);
            return false;
        }
    }

    private void SetTextureAttributes(nint handle, KnifePreset preset)
    {
        SetTextureAttributes(handle, preset.Paint, preset.Seed, preset.Wear);
    }

    private void SetTextureAttributes(nint handle, int paint, int seed, float wear)
    {
        if (handle == nint.Zero) return;
        _setAttrByName!.Invoke(handle, "set item texture prefab", paint);
        _setAttrByName.Invoke(handle, "set item texture seed", seed);
        _setAttrByName.Invoke(handle, "set item texture wear", wear);
    }

    private bool TryApplyStickers(ushort defIndex, CEconItemView item, KnifePreset preset)
    {
        // Keep the preview framework compiled, but never enter its native-write path in 1.4.2.6.
        if (!StickerReleaseEnabled) return true;

        if (!StickerAttributePlanner.TryBuild(
                defIndex, _config.StickersEnabled, preset.Stickers, _validStickers, _validStickerWeapons,
                out var attributes, out string error))
        {
            LogApplyError($"stickers for defindex {defIndex}", new InvalidDataException(error));
            return false;
        }
        if (attributes.Count == 0) return true;

        try
        {
            nint handle = item.NetworkedDynamicAttributes.Handle;
            foreach (var attribute in attributes)
                _setAttrByName!.Invoke(handle, attribute.Name, attribute.Value);
            return true;
        }
        catch (Exception ex)
        {
            LogApplyError($"stickers for defindex {defIndex}", ex);
            return false;
        }
    }

    private void RestoreBaseAttributes(CBasePlayerWeapon weapon, CEconItemView item, KnifePreset preset)
    {
        item.AttributeList.Attributes.RemoveAll();
        item.NetworkedDynamicAttributes.Attributes.RemoveAll();
        SetTextureAttributes(item.NetworkedDynamicAttributes.Handle, preset);
        SetTextureAttributes(item.AttributeList.Handle, preset);
        if (preset.StatTrakEnabled && !preset.SouvenirEnabled) ApplyStatTrak(weapon, preset);
    }

    private void ApplyStatTrak(CBasePlayerWeapon weapon, KnifePreset preset)
    {
        if (_setAttrByName == null) return;
        var item = weapon.AttributeManager?.Item;
        if (item == null || !HasReadyAttributeLists(item)) return;

        nint networkedHandle = item.NetworkedDynamicAttributes.Handle;
        nint attributeHandle = item.AttributeList.Handle;
        if (networkedHandle == nint.Zero || attributeHandle == nint.Zero) return;

        float count = BitConverter.Int32BitsToSingle(preset.StatTrakCount);
        _setAttrByName.Invoke(networkedHandle, "kill eater", count);
        _setAttrByName.Invoke(networkedHandle, "kill eater score type", 0);
        _setAttrByName.Invoke(attributeHandle, "kill eater", count);
        _setAttrByName.Invoke(attributeHandle, "kill eater score type", 0);
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
    }

    private bool ValidatePreset(ushort defIndex, KnifePreset preset)
    {
        if (preset.Paint <= 0 || preset.Seed is < 0 or > 1000 || preset.Wear is < 0 or > 1)
            return false;
        if (_validPaints.TryGetValue(defIndex, out var paints) && !paints.Contains(preset.Paint))
            return false;
        if (preset.StatTrakEnabled && preset.SouvenirEnabled)
            return false;
        WeaponSkinEntry? skin = FindSkin(defIndex, preset.Paint);
        if (skin == null) return IsKnifeDefIndex(defIndex);
        return (!preset.StatTrakEnabled || skin.StatTrak) &&
               (!preset.SouvenirEnabled || skin.Souvenir) &&
               preset.Wear >= skin.MinWear && preset.Wear <= skin.MaxWear;
    }

    private bool CanApplyToPlayer(CCSPlayerController? player) =>
        _config.Enabled && _config.ApplyToHumanPlayers && player is { IsValid: true, IsBot: false, IsHLTV: false };

    private static CCSPlayerController? ResolvePlayer(nint handle)
    {
        if (handle == nint.Zero) return null;
        try
        {
            var player = new CCSPlayerController(handle);
            return player.IsValid ? player : null;
        }
        catch
        {
            return null;
        }
    }

    private static CosmeticTeam? GetCosmeticTeam(CCSPlayerController? player) => player?.Team switch
    {
        CsTeam.CounterTerrorist => CosmeticTeam.Ct,
        CsTeam.Terrorist => CosmeticTeam.T,
        _ => null,
    };

    private static bool IsKnifeName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && (name.Contains("knife", StringComparison.OrdinalIgnoreCase)
                                             || name.Contains("bayonet", StringComparison.OrdinalIgnoreCase));

    private static bool IsKnifeDefIndex(ushort defIndex) => defIndex is >= 500 and <= 526;

    private CCSPlayerController? GetEligibleHumanOwner(CBasePlayerWeapon weapon)
    {
        try
        {
            var owner = weapon.OwnerEntity.Value;
            if (owner == null || !owner.IsValid) return null;
            var pawn = new CCSPlayerPawn(owner.Handle);
            var controller = pawn.Controller.Value;
            if (controller == null || !controller.IsValid) return null;
            var player = new CCSPlayerController(controller.Handle);
            return CanApplyToPlayer(player) ? player : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasReadyAttributeLists(CEconItemView item) =>
        item.AttributeList.Handle != nint.Zero &&
        item.NetworkedDynamicAttributes.Handle != nint.Zero;

    private void AssignItemId(CEconItemView item)
    {
        ulong id = unchecked(_nextItemId++);
        item.ItemID = id;
        item.ItemIDLow = (uint)(id & 0xFFFFFFFF);
        item.ItemIDHigh = (uint)(id >> 32);
    }

    private void LoadConfig()
    {
        try
        {
            _loadedConfigSchema = KnifeConfig.CurrentSchemaVersion;
            _loadedGunConfigSchema = KnifeConfig.CurrentSchemaVersion;
            _stickersSanitizedDuringLoad = false;
            if (!File.Exists(ConfigPath))
            {
                _config = new KnifeConfig();
                LoadGunConfig();
                SaveConfig();
                return;
            }

            string text = File.ReadAllText(ConfigPath);
            bool stickersSanitized;
            using (var document = JsonDocument.Parse(text))
            {
                _loadedConfigSchema = document.RootElement.TryGetProperty("schema_version", out var schema)
                    ? schema.GetInt32() : 1;
                if (_loadedConfigSchema is < 1 or > KnifeConfig.CurrentSchemaVersion)
                    throw new InvalidDataException($"Unsupported cosmetics schema {_loadedConfigSchema}");
                bool isTeamSchema = _loadedConfigSchema is 2 or 3 && document.RootElement.TryGetProperty("loadouts", out _);
                if (_loadedConfigSchema is 2 or 3 && !isTeamSchema)
                    throw new InvalidDataException($"Cosmetics schema {_loadedConfigSchema} has no team loadouts");
                text = SanitizeConfigStickerFields(text, gunFile: false, out stickersSanitized);
                if (isTeamSchema)
                    _config = JsonSerializer.Deserialize<KnifeConfig>(text, JsonOptions) ?? new KnifeConfig();
                else
                {
                    var legacy = JsonSerializer.Deserialize<LegacyKnifeConfig>(text, JsonOptions) ?? new LegacyKnifeConfig();
                    _config = KnifeConfig.FromLegacy(legacy);
                }
            }
            LoadGunConfig();
            _config.Normalize();
            _applyErrorThrottle.Reset();
            if (_loadedConfigSchema < KnifeConfig.CurrentSchemaVersion ||
                _loadedGunConfigSchema < KnifeConfig.CurrentSchemaVersion || stickersSanitized || _stickersSanitizedDuringLoad)
                SaveConfig();
        }
        catch (Exception ex)
        {
            _config = new KnifeConfig();
            LoadGunConfig();
            Logger.LogError("[PlayerKnifeCustomizer] Config load failed: {Message}", ex.Message);
        }
    }

    private void SaveConfig()
    {
        _config.Normalize();
        BackupVersionedFile(ConfigPath, _loadedConfigSchema);
        BackupVersionedFile(GunConfigPath, _loadedGunConfigSchema);
        WriteJsonAtomic(ConfigPath, _config);
        SaveGunConfig();
        _loadedConfigSchema = KnifeConfig.CurrentSchemaVersion;
        _loadedGunConfigSchema = KnifeConfig.CurrentSchemaVersion;
    }

    private void LoadGunConfig()
    {
        if (!File.Exists(GunConfigPath))
            return;
        try
        {
            string text = File.ReadAllText(GunConfigPath);
            using var document = JsonDocument.Parse(text);
            _loadedGunConfigSchema = document.RootElement.TryGetProperty("schema_version", out var schema)
                ? schema.GetInt32() : 1;
            if (_loadedGunConfigSchema is < 1 or > KnifeConfig.CurrentSchemaVersion)
                throw new InvalidDataException($"Unsupported gun cosmetics schema {_loadedGunConfigSchema}");
            text = SanitizeConfigStickerFields(text, gunFile: true, out bool stickersSanitized);
            if (_loadedGunConfigSchema is 2 or 3)
            {
                var guns = JsonSerializer.Deserialize<TeamGunConfig>(text, JsonOptions) ?? new TeamGunConfig();
                _config.Loadouts.Ct.GunPresets = guns.Ct ?? new Dictionary<ushort, KnifePreset>();
                _config.Loadouts.T.GunPresets = guns.T ?? new Dictionary<ushort, KnifePreset>();
                _config.SharedWeaponLinks = guns.SharedWeaponLinks ?? new Dictionary<ushort, bool>();
            }
            else
            {
                var legacy = JsonSerializer.Deserialize<Dictionary<ushort, KnifePreset>>(text, JsonOptions)
                    ?? new Dictionary<ushort, KnifePreset>();
                _config.ApplyLegacyGuns(legacy);
            }
            _config.Normalize();
            _stickersSanitizedDuringLoad |= stickersSanitized;
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerKnifeCustomizer] Gun preset config load failed: {Message}", ex.Message);
        }
    }

    private void SaveGunConfig()
    {
        WriteJsonAtomic(GunConfigPath, TeamGunConfig.From(_config));
    }

    private string SanitizeConfigStickerFields(string text, bool gunFile, out bool changed)
    {
        JsonNode root = JsonNode.Parse(text) ?? throw new InvalidDataException("Cosmetics configuration is empty");
        changed = false;
        if (gunFile)
        {
            if (_loadedGunConfigSchema is 2 or 3)
            {
                changed |= SanitizePresetMap(root["ct"], allowStickers: true);
                changed |= SanitizePresetMap(root["t"], allowStickers: true);
            }
            else
                changed |= SanitizePresetMap(root, allowStickers: true);
        }
        else
        {
            changed |= SanitizePresetMap(root["presets"], allowStickers: false);
            changed |= SanitizePresetMap(root["gun_presets"], allowStickers: true);
            foreach (string side in new[] { "ct", "t" })
            {
                JsonNode? loadout = root["loadouts"]?[side];
                changed |= SanitizePresetMap(loadout?["knife_presets"], allowStickers: false);
                changed |= SanitizePresetMap(loadout?["gun_presets"], allowStickers: true);
            }
        }
        return root.ToJsonString(JsonOptions);
    }

    private bool SanitizePresetMap(JsonNode? node, bool allowStickers)
    {
        if (node is not JsonObject presets) return false;
        bool changed = false;
        foreach (var (key, value) in presets.ToArray())
        {
            if (value is not JsonObject preset || !preset.TryGetPropertyValue("stickers", out JsonNode? stickerNode))
                continue;
            bool valid = false;
            if (allowStickers && ushort.TryParse(key, out ushort defIndex) && stickerNode is JsonArray)
            {
                try
                {
                    var stickers = stickerNode.Deserialize<List<StickerPreset>>(JsonOptions) ?? new List<StickerPreset>();
                    valid = StickerAttributePlanner.TryBuild(
                        defIndex, true, stickers, _validStickers, _validStickerWeapons, out _, out _);
                }
                catch (JsonException)
                {
                    valid = false;
                }
            }
            else if (!allowStickers && stickerNode is JsonArray array && array.Count == 0)
                valid = true;

            if (valid) continue;
            preset["stickers"] = new JsonArray();
            changed = true;
        }
        return changed;
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temp, path, true);
    }

    private static void BackupVersionedFile(string path, int sourceSchema)
    {
        if (sourceSchema >= KnifeConfig.CurrentSchemaVersion || !File.Exists(path)) return;
        int version = Math.Max(1, sourceSchema);
        string backup = path + $".v{version}.bak";
        if (!File.Exists(backup)) File.Copy(path, backup);
    }

    private void LoadCatalog()
    {
        _validPaints.Clear();
        _skinCatalog.Clear();
        _legacyPaints.Clear();
        if (!File.Exists(CatalogPath)) return;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(CatalogPath));
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                ushort defIndex = (ushort)ReadInt(entry.GetProperty("weapon_defindex"));
                int paint = ReadInt(entry.GetProperty("paint"));
                if (defIndex == 0 || paint <= 0) continue;
                var skin = new WeaponSkinEntry
                {
                    WeaponDefIndex = defIndex,
                    Paint = paint,
                    Name = entry.TryGetProperty("name", out var name) ? name.GetString() ?? $"Paint Kit {paint}" : $"Paint Kit {paint}",
                    MinWear = entry.TryGetProperty("min_wear", out var minWear) ? minWear.GetSingle() : 0f,
                    MaxWear = entry.TryGetProperty("max_wear", out var maxWear) ? maxWear.GetSingle() : 1f,
                    StatTrak = entry.TryGetProperty("stattrak", out var statTrak) && statTrak.GetBoolean(),
                    Souvenir = entry.TryGetProperty("souvenir", out var souvenir) && souvenir.GetBoolean(),
                };
                if (entry.TryGetProperty("legacy_model", out var legacy) && legacy.GetBoolean())
                    _legacyPaints.Add((defIndex, paint));
                if (!_validPaints.TryGetValue(defIndex, out var paints))
                    _validPaints[defIndex] = paints = new HashSet<int>();
                paints.Add(paint);
                if (!_skinCatalog.TryGetValue(defIndex, out var skins))
                    _skinCatalog[defIndex] = skins = new List<WeaponSkinEntry>();
                skins.Add(skin);
            }
            foreach (var skins in _skinCatalog.Values)
                skins.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCulture));
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerKnifeCustomizer] Skin catalog load failed: {Message}", ex.Message);
        }

        static int ReadInt(JsonElement element) => element.ValueKind == JsonValueKind.Number
            ? element.GetInt32()
            : int.TryParse(element.GetString(), out int value) ? value : 0;
    }

    private void LoadStickerCatalog()
    {
        _validStickers.Clear();
        if (!File.Exists(StickerCatalogPath)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(StickerCatalogPath));
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("id", out var idElement)) continue;
                uint id = idElement.ValueKind == JsonValueKind.Number
                    ? idElement.GetUInt32()
                    : uint.TryParse(idElement.GetString(), out uint value) ? value : 0;
                if (id > 0) _validStickers.Add(id);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerKnifeCustomizer] Sticker catalog load failed: {Message}", ex.Message);
        }
    }

    private void LoadStickerWeaponCatalog()
    {
        _validStickerWeapons.Clear();
        if (!File.Exists(StickerWeaponCatalogPath)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(StickerWeaponCatalogPath));
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("id", out var idElement)) continue;
                ushort id = idElement.ValueKind == JsonValueKind.Number
                    ? idElement.GetUInt16()
                    : ushort.TryParse(idElement.GetString(), out ushort value) ? value : (ushort)0;
                if (id > 0) _validStickerWeapons.Add(id);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerKnifeCustomizer] Sticker weapon catalog load failed: {Message}", ex.Message);
        }
    }

    private WeaponSkinEntry? FindSkin(ushort defIndex, int paint) =>
        _skinCatalog.TryGetValue(defIndex, out var skins)
            ? skins.FirstOrDefault(skin => skin.Paint == paint)
            : null;

    private void OnReloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        LoadCatalog();
        LoadStickerCatalog();
        LoadStickerWeaponCatalog();
        LoadConfig();
        string restart = _config.Enabled && _setAttrByName == null ? "; restart CS2 to initialize runtime hooks" : string.Empty;
        command.ReplyToCommand($"[PlayerKnifeCustomizer] reloaded; enabled={_config.Enabled}, stickers={StickerReleaseEnabled && _config.StickersEnabled}, sticker_catalog={_validStickers.Count}, ct_knives={_config.Loadouts.Ct.KnifePresets.Count}, t_knives={_config.Loadouts.T.KnifePresets.Count}, ct_guns={_config.Loadouts.Ct.GunPresets.Count}, t_guns={_config.Loadouts.T.GunPresets.Count}, music={_config.MusicKitId}{restart}");
    }

    private void OnStatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        command.ReplyToCommand($"[PlayerKnifeCustomizer] enabled={_config.Enabled}, stickers={StickerReleaseEnabled && _config.StickersEnabled}, signature={(_setAttrByName == null ? "missing" : "loaded")}, ct_knives={_config.Loadouts.Ct.KnifePresets.Count}, t_knives={_config.Loadouts.T.KnifePresets.Count}, ct_guns={_config.Loadouts.Ct.GunPresets.Count}, t_guns={_config.Loadouts.T.GunPresets.Count}, music={_config.MusicKitId}, catalog={_skinCatalog.Values.Sum(skins => skins.Count)}, sticker_catalog={_validStickers.Count}, active_generations={_applyTracker.ActiveCount}, schedules={_applyTracker.Schedules}, phase_completions={_applyTracker.PhaseCompletions}, retry_exhaustions={_applyTracker.RetryExhaustions}, context_invalidations={_applyTracker.ContextInvalidations}");
    }

    private void LogApplyError(string operation, Exception ex)
    {
        ApplyErrorDecision decision = _applyErrorThrottle.Check(operation, DateTimeOffset.UtcNow);
        if (!decision.ShouldLog) return;
        Logger.LogError(ex,
            "[PlayerKnifeCustomizer] Apply failed during {Operation}; suppressed_since_last={Suppressed}: {Message}",
            operation, decision.Suppressed, ex.Message);
    }
}

[Flags]
public enum CosmeticApplyPhase
{
    None = 0,
    Knife = 1,
    Gloves = 2,
    Guns = 4,
    Music = 8,
    All = Knife | Gloves | Guns | Music,
}

public sealed class ApplyGenerationTracker
{
    private sealed class State
    {
        public required long Generation { get; init; }
        public required CosmeticApplyPhase Pending { get; set; }
        public nint PawnHandle { get; set; }
        public int? Team { get; set; }
        public bool ExhaustionRecorded { get; set; }
        public bool ReequipIssued { get; set; }
    }

    private readonly Dictionary<nint, State> _states = new();
    private long _nextGeneration;
    public int ContextInvalidations { get; private set; }
    public long Schedules { get; private set; }
    public long PhaseCompletions { get; private set; }
    public long RetryExhaustions { get; private set; }
    public int ActiveCount => _states.Count;

    public long Begin(nint playerHandle, CosmeticApplyPhase phases)
    {
        long generation = ++_nextGeneration;
        if (_states.TryGetValue(playerHandle, out var previous))
            phases |= previous.Pending;
        _states[playerHandle] = new State { Generation = generation, Pending = phases };
        Schedules++;
        return generation;
    }

    public bool IsCurrent(nint playerHandle, long generation) =>
        _states.TryGetValue(playerHandle, out var state) && state.Generation == generation;

    public bool TryBindContext(nint playerHandle, long generation, nint pawnHandle, int team)
    {
        if (!_states.TryGetValue(playerHandle, out var state) || state.Generation != generation)
            return false;
        if ((state.PawnHandle != nint.Zero && state.PawnHandle != pawnHandle) ||
            (state.Team.HasValue && state.Team.Value != team))
        {
            ContextInvalidations++;
            _states.Remove(playerHandle);
            return false;
        }
        state.PawnHandle = pawnHandle;
        state.Team = team;
        return true;
    }

    public bool IsPending(nint playerHandle, long generation, CosmeticApplyPhase phase) =>
        _states.TryGetValue(playerHandle, out var state) && state.Generation == generation &&
        (state.Pending & phase) != 0;

    public bool HasPending(nint playerHandle, long generation) =>
        _states.TryGetValue(playerHandle, out var state) && state.Generation == generation &&
        state.Pending != CosmeticApplyPhase.None;

    public bool Complete(nint playerHandle, long generation, CosmeticApplyPhase phase)
    {
        if (!_states.TryGetValue(playerHandle, out var state) || state.Generation != generation)
            return false;
        state.Pending &= ~phase;
        PhaseCompletions++;
        return true;
    }

    public bool MarkRetryExhausted(nint playerHandle, long generation)
    {
        if (!_states.TryGetValue(playerHandle, out var state) || state.Generation != generation ||
            state.Pending == CosmeticApplyPhase.None || state.ExhaustionRecorded)
            return false;
        state.ExhaustionRecorded = true;
        RetryExhaustions++;
        return true;
    }

    public bool TryMarkReequip(nint playerHandle, long generation)
    {
        if (!_states.TryGetValue(playerHandle, out var state) || state.Generation != generation || state.ReequipIssued)
            return false;
        state.ReequipIssued = true;
        return true;
    }

    public void Cancel(nint playerHandle) => _states.Remove(playerHandle);
    public void CancelAll() => _states.Clear();
}

public static class ApplyPipelineContext
{
    // Deathmatch and retake can publish player_spawn before the replacement
    // Pawn and its econ services are fully live. Keep retries bounded while
    // allowing the replacement Pawn enough time to become authoritative.
    public static readonly float[] RetryDelays = [0.10f, 0.25f, 0.50f, 0.90f];

    public static bool IsReady(bool pawnAlive, bool pawnValid, nint pawnHandle, CosmeticTeam? team) =>
        pawnAlive && pawnValid && pawnHandle != nint.Zero && team.HasValue;
}

public readonly record struct ApplyErrorDecision(bool ShouldLog, int Suppressed);

public sealed class ApplyErrorThrottle(TimeSpan interval)
{
    private sealed class Entry
    {
        public required DateTimeOffset LastLogged { get; set; }
        public int Suppressed { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public void Reset() => _entries.Clear();

    public ApplyErrorDecision Check(string operation, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(operation, out Entry? entry))
        {
            _entries[operation] = new Entry { LastLogged = now };
            return new ApplyErrorDecision(true, 0);
        }
        if (now - entry.LastLogged < interval)
        {
            entry.Suppressed++;
            return new ApplyErrorDecision(false, entry.Suppressed);
        }

        int suppressed = entry.Suppressed;
        entry.LastLogged = now;
        entry.Suppressed = 0;
        return new ApplyErrorDecision(true, suppressed);
    }
}

public enum CosmeticTeam { Ct, T }

public enum WeaponAvailability { Ct, T, Shared }

public sealed class TeamLoadoutCollection
{
    [JsonPropertyName("ct")]
    public TeamLoadout Ct { get; set; } = new();

    [JsonPropertyName("t")]
    public TeamLoadout T { get; set; } = new();

    public TeamLoadout For(CosmeticTeam team) => team == CosmeticTeam.Ct ? Ct : T;
}

public sealed class TeamLoadout
{
    [JsonPropertyName("default_knife_defindex")]
    public ushort DefaultKnifeDefIndex { get; set; }

    [JsonPropertyName("knife_presets")]
    public Dictionary<ushort, KnifePreset> KnifePresets { get; set; } = new();

    [JsonPropertyName("glove")]
    public GlovePreset Glove { get; set; } = new();

    [JsonPropertyName("gun_presets")]
    public Dictionary<ushort, KnifePreset> GunPresets { get; set; } = new();

    public TeamLoadout Clone() => new()
    {
        DefaultKnifeDefIndex = DefaultKnifeDefIndex,
        KnifePresets = KnifePresets.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
        Glove = Glove.Clone(),
        GunPresets = GunPresets.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
    };

    public void Normalize()
    {
        KnifePresets ??= new Dictionary<ushort, KnifePreset>();
        GunPresets ??= new Dictionary<ushort, KnifePreset>();
        Glove ??= new GlovePreset();
        foreach (var preset in KnifePresets.Values.Concat(GunPresets.Values)) preset.Normalize();
        Glove.Normalize();
        if (DefaultKnifeDefIndex != 0 && !KnifePresets.ContainsKey(DefaultKnifeDefIndex))
            DefaultKnifeDefIndex = 0;
    }
}

public sealed class KnifeConfig
{
    public const int CurrentSchemaVersion = 3;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("apply_to_human_players")]
    public bool ApplyToHumanPlayers { get; set; } = true;

    [JsonPropertyName("apply_on_pickup")]
    public bool ApplyOnPickup { get; set; } = true;

    [JsonPropertyName("music_kit_id")]
    public int MusicKitId { get; set; }

    [JsonPropertyName("loadouts")]
    public TeamLoadoutCollection Loadouts { get; set; } = new();

    [JsonPropertyName("shared_weapon_links")]
    public Dictionary<ushort, bool> SharedWeaponLinks { get; set; } = new();

    [JsonPropertyName("stickers_enabled")]
    public bool StickersEnabled { get; set; }

    public static KnifeConfig FromLegacy(LegacyKnifeConfig legacy)
    {
        var baseLoadout = new TeamLoadout
        {
            DefaultKnifeDefIndex = legacy.DefaultKnifeDefIndex,
            KnifePresets = (legacy.Presets ?? new()).ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
            Glove = (legacy.Glove ?? new GlovePreset()).Clone(),
        };
        var config = new KnifeConfig
        {
            Enabled = legacy.Enabled,
            ApplyToHumanPlayers = legacy.ApplyToHumanPlayers,
            ApplyOnPickup = legacy.ApplyOnPickup,
            MusicKitId = legacy.MusicKitId,
            Loadouts = new TeamLoadoutCollection { Ct = baseLoadout.Clone(), T = baseLoadout.Clone() },
        };
        config.ApplyLegacyGuns(legacy.GunPresets ?? new Dictionary<ushort, KnifePreset>());
        config.Normalize();
        return config;
    }

    public void ApplyLegacyGuns(Dictionary<ushort, KnifePreset> guns)
    {
        Loadouts.Ct.GunPresets.Clear();
        Loadouts.T.GunPresets.Clear();
        foreach (var (defIndex, preset) in guns)
        {
            switch (WeaponPresetResolver.GetAvailability(defIndex))
            {
                case WeaponAvailability.Ct:
                    Loadouts.Ct.GunPresets[defIndex] = preset.Clone();
                    break;
                case WeaponAvailability.T:
                    Loadouts.T.GunPresets[defIndex] = preset.Clone();
                    break;
                default:
                    Loadouts.Ct.GunPresets[defIndex] = preset.Clone();
                    Loadouts.T.GunPresets[defIndex] = preset.Clone();
                    SharedWeaponLinks[defIndex] = true;
                    break;
            }
        }
    }

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        Loadouts ??= new TeamLoadoutCollection();
        Loadouts.Ct ??= new TeamLoadout();
        Loadouts.T ??= new TeamLoadout();
        SharedWeaponLinks ??= new Dictionary<ushort, bool>();
        MusicKitId = Math.Clamp(MusicKitId, 0, ushort.MaxValue);
        Loadouts.Ct.Normalize();
        Loadouts.T.Normalize();
        foreach (ushort defIndex in WeaponPresetResolver.SharedWeapons)
            SharedWeaponLinks.TryAdd(defIndex, true);
        foreach (var (defIndex, linked) in SharedWeaponLinks.ToArray())
        {
            if (!linked || WeaponPresetResolver.GetAvailability(defIndex) != WeaponAvailability.Shared) continue;
            bool hasCt = Loadouts.Ct.GunPresets.TryGetValue(defIndex, out var ct);
            bool hasT = Loadouts.T.GunPresets.TryGetValue(defIndex, out var t);
            if (hasCt && !hasT) Loadouts.T.GunPresets[defIndex] = ct!.Clone();
            else if (!hasCt && hasT) Loadouts.Ct.GunPresets[defIndex] = t!.Clone();
            else if (hasCt && hasT && !ct!.ValueEquals(t!)) Loadouts.T.GunPresets[defIndex] = ct.Clone();
        }
    }
}

public sealed class LegacyKnifeConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("apply_to_human_players")] public bool ApplyToHumanPlayers { get; set; } = true;
    [JsonPropertyName("apply_on_pickup")] public bool ApplyOnPickup { get; set; } = true;
    [JsonPropertyName("default_knife_defindex")] public ushort DefaultKnifeDefIndex { get; set; }
    [JsonPropertyName("presets")] public Dictionary<ushort, KnifePreset> Presets { get; set; } = new();
    [JsonPropertyName("gun_presets")] public Dictionary<ushort, KnifePreset> GunPresets { get; set; } = new();
    [JsonPropertyName("music_kit_id")] public int MusicKitId { get; set; }
    [JsonPropertyName("glove")] public GlovePreset Glove { get; set; } = new();
}

public sealed class TeamGunConfig
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = KnifeConfig.CurrentSchemaVersion;
    [JsonPropertyName("ct")] public Dictionary<ushort, KnifePreset> Ct { get; set; } = new();
    [JsonPropertyName("t")] public Dictionary<ushort, KnifePreset> T { get; set; } = new();
    [JsonPropertyName("shared_weapon_links")] public Dictionary<ushort, bool> SharedWeaponLinks { get; set; } = new();

    public static TeamGunConfig From(KnifeConfig config) => new()
    {
        Ct = config.Loadouts.Ct.GunPresets,
        T = config.Loadouts.T.GunPresets,
        SharedWeaponLinks = config.SharedWeaponLinks,
    };
}

public static class WeaponPresetResolver
{
    private static readonly HashSet<ushort> CtOnly = [3, 8, 10, 16, 27, 32, 34, 38, 60, 61];
    private static readonly HashSet<ushort> TOnly = [4, 7, 11, 13, 17, 29, 30, 39];
    public static readonly ushort[] SharedWeapons = [1, 2, 9, 14, 19, 23, 24, 25, 26, 28, 31, 33, 35, 36, 40, 63, 64];

    public static WeaponAvailability GetAvailability(ushort defIndex) => CtOnly.Contains(defIndex)
        ? WeaponAvailability.Ct
        : TOnly.Contains(defIndex) ? WeaponAvailability.T : WeaponAvailability.Shared;

    public static bool HasAnyGunPreset(KnifeConfig config, ushort defIndex) =>
        config.Loadouts.Ct.GunPresets.ContainsKey(defIndex) || config.Loadouts.T.GunPresets.ContainsKey(defIndex);

    public static bool TryResolveGunPreset(KnifeConfig config, ushort defIndex, CosmeticTeam? currentTeam, out KnifePreset preset)
    {
        if (currentTeam == null)
        {
            preset = null!;
            return false;
        }
        var availability = GetAvailability(defIndex);
        var primary = availability switch
        {
            WeaponAvailability.Ct => config.Loadouts.Ct.GunPresets,
            WeaponAvailability.T => config.Loadouts.T.GunPresets,
            _ => config.Loadouts.For(currentTeam.Value).GunPresets,
        };
        if (primary.TryGetValue(defIndex, out preset!)) return true;
        if (availability == WeaponAvailability.Shared && config.SharedWeaponLinks.GetValueOrDefault(defIndex, true))
            return config.Loadouts.For(currentTeam == CosmeticTeam.Ct ? CosmeticTeam.T : CosmeticTeam.Ct)
                .GunPresets.TryGetValue(defIndex, out preset!);
        preset = null!;
        return false;
    }
}

public sealed class GlovePreset
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("defindex")]
    public ushort DefIndex { get; set; } = 5030;

    [JsonPropertyName("paint")]
    public int Paint { get; set; } = 10048;

    [JsonPropertyName("seed")]
    public int Seed { get; set; }

    [JsonPropertyName("wear")]
    public float Wear { get; set; } = 0.01f;

    public GlovePreset Clone() => new()
    {
        Enabled = Enabled, DefIndex = DefIndex, Paint = Paint, Seed = Seed, Wear = Wear,
    };

    public void Normalize()
    {
        Seed = Math.Clamp(Seed, 0, 1000);
        Wear = Math.Clamp(Wear, 0f, 1f);
        if (Enabled && DefIndex == 0 && Paint == 0)
        {
            DefIndex = 5030;
            Paint = 10048;
            Wear = 0.01f;
        }
    }
}

public sealed class KnifePreset
{
    [JsonPropertyName("paint")]
    public int Paint { get; set; }

    [JsonPropertyName("seed")]
    public int Seed { get; set; }

    [JsonPropertyName("wear")]
    public float Wear { get; set; } = 0.01f;

    [JsonPropertyName("name_tag")]
    public string? NameTag { get; set; } = string.Empty;

    [JsonPropertyName("stattrak_enabled")]
    public bool StatTrakEnabled { get; set; }

    [JsonPropertyName("stattrak_count")]
    public int StatTrakCount { get; set; }

    [JsonPropertyName("souvenir_enabled")]
    public bool SouvenirEnabled { get; set; }

    [JsonPropertyName("stickers")]
    public List<StickerPreset> Stickers { get; set; } = new();

    public KnifePreset Clone() => new()
    {
        Paint = Paint, Seed = Seed, Wear = Wear, NameTag = NameTag,
        StatTrakEnabled = StatTrakEnabled, StatTrakCount = StatTrakCount,
        SouvenirEnabled = SouvenirEnabled,
        Stickers = (Stickers ?? []).Select(sticker => sticker.Clone()).ToList(),
    };

    public bool ValueEquals(KnifePreset other) => Paint == other.Paint && Seed == other.Seed
        && Wear.Equals(other.Wear) && NameTag == other.NameTag
        && StatTrakEnabled == other.StatTrakEnabled && StatTrakCount == other.StatTrakCount
        && SouvenirEnabled == other.SouvenirEnabled
        && (Stickers ?? []).Count == (other.Stickers ?? []).Count
        && (Stickers ?? []).Zip(other.Stickers ?? []).All(pair => pair.First.ValueEquals(pair.Second));

    public void Normalize()
    {
        Seed = Math.Clamp(Seed, 0, 1000);
        Wear = Math.Clamp(Wear, 0f, 1f);
        StatTrakCount = Math.Max(0, StatTrakCount);
        if (SouvenirEnabled) StatTrakEnabled = false;
        if (NameTag?.Length > 20) NameTag = NameTag[..20];
        Stickers ??= new List<StickerPreset>();
        Stickers = Stickers.OrderBy(sticker => sticker.Slot).ToList();
    }
}

public sealed class StickerPreset
{
    [JsonPropertyName("slot")] public byte Slot { get; set; }
    [JsonPropertyName("id")] public uint Id { get; set; }
    [JsonPropertyName("wear")] public float Wear { get; set; }
    [JsonPropertyName("scale")] public float Scale { get; set; } = 1f;
    [JsonPropertyName("rotation")] public float Rotation { get; set; }
    [JsonPropertyName("offset_x")] public float OffsetX { get; set; }
    [JsonPropertyName("offset_y")] public float OffsetY { get; set; }
    [JsonPropertyName("custom_position")] public bool CustomPosition { get; set; }

    public StickerPreset Clone() => new()
    {
        Slot = Slot,
        Id = Id,
        Wear = Wear,
        Scale = Scale,
        Rotation = Rotation,
        OffsetX = OffsetX,
        OffsetY = OffsetY,
        CustomPosition = CustomPosition,
    };

    public bool ValueEquals(StickerPreset other) => Slot == other.Slot && Id == other.Id
        && Wear.Equals(other.Wear) && Scale.Equals(other.Scale) && Rotation.Equals(other.Rotation)
        && OffsetX.Equals(other.OffsetX) && OffsetY.Equals(other.OffsetY)
        && CustomPosition == other.CustomPosition;
}

public sealed class WeaponSkinEntry
{
    public ushort WeaponDefIndex { get; init; }
    public int Paint { get; init; }
    public string Name { get; init; } = string.Empty;
    public float MinWear { get; init; }
    public float MaxWear { get; init; } = 1f;
    public bool StatTrak { get; init; }
    public bool Souvenir { get; init; }
}
