using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

public class BotState : BasePlugin
{
    public override string ModuleName        => "Smarter-Bot";
    public override string ModuleVersion     => "1.4.0";
    public override string ModuleAuthor      => "ed0ard";
    public override string ModuleDescription => "Make bots smarter";

    private const float ExpandedValue = 4000f;
    private const float NormalValue   = 100f;
    private const float RestoreDelay  = 1.0f;

    private bool _isExpanded = false;
    private ConVar? _smokeConVar;

    private readonly Random _random = new Random();

    public override void Load(bool hotReload)
    {
        _smokeConVar = ConVar.Find("bot_max_visible_smoke_length");
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterListener<Listeners.OnTick>(OnTick);
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo _)
    {
        try
        {
            var victim = @event.Userid;
            if (victim == null || !victim.IsValid || !victim.IsBot) return HookResult.Continue;

            if (!_isExpanded)
            {
                _isExpanded = true;
                SetSmokeLength(ExpandedValue);
                AddTimer(RestoreDelay, () =>
                {
                    SetSmokeLength(NormalValue);
                    _isExpanded = false;
                });
            }
        }
        catch { }
        return HookResult.Continue;
    }

    private void SetSmokeLength(float value)
    {
        if (_smokeConVar != null)
            _smokeConVar.SetValue(value);
        else
            Server.ExecuteCommand($"bot_max_visible_smoke_length {value}");
    }

    public override void Unload(bool hotReload)
    {
        SetSmokeLength(NormalValue);
    }
//---------------------------------------------------------------------------------------
    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player is null || !player.IsValid || !player.IsBot)
            return HookResult.Continue;

        bool isImmune = _random.NextDouble() <= 0.7;
        
        if (isImmune)
        {
            @event.BlindDuration = 0f;
            var pawn = player.PlayerPawn?.Value;
            if (pawn != null && pawn.IsValid)
            {
                ref float blindStartTime = ref pawn.BlindStartTime;
                blindStartTime = 0f;
                
                ref float blindUntilTime = ref pawn.BlindUntilTime;
                blindUntilTime = 0f;
                
                ref float flashDuration = ref pawn.FlashDuration;
                flashDuration = 0f;

                ref float flashMaxAlpha = ref pawn.FlashMaxAlpha;
                flashMaxAlpha = 0f;   
            }
        }
        
        return HookResult.Continue;
    }
//---------------------------------------------------------------------------------------
    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot)
            return HookResult.Continue;

        Server.NextFrame(() =>
        {
            if (player == null || !player.IsValid) return;
            ApplyBotState(player);
        });

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot) continue;
            ApplyBotState(player);
        }
        return HookResult.Continue;
    }

    private void OnTick()
    {
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot)
                continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid) 
                continue;

            var bot = pawn.Bot;
            if (bot == null) 
                continue;

            ref bool isSleeping = ref bot.IsSleeping;
            isSleeping = false;

            ref bool allowActive = ref bot.AllowActive;
            allowActive = true;
            
            ref bool isRapidFiring = ref bot.IsRapidFiring;
            isRapidFiring = true;

            ref float fireWeaponTimestamp = ref bot.FireWeaponTimestamp;
            fireWeaponTimestamp = 0.0f;

            ref float duration = ref bot.IgnoreEnemiesTimer.Duration;
            duration = 0.0f;
        }
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot)
                continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            var bot = pawn.Bot;
            if (bot == null)
                continue;

            CountdownTimer hurryTimer = bot.HurryTimer;

            ref float duration = ref hurryTimer.Duration;
            duration = 40.0f;

            ref float timestamp = ref hurryTimer.Timestamp;
            timestamp = Server.CurrentTime;

            ref float timescale = ref hurryTimer.Timescale;
            timescale = 1.0f;
        }
        return HookResult.Continue;
    }

    private static void ApplyBotState(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;

        var bot = pawn.Bot;
        if (bot == null) return;

        ref float safeTime = ref bot.SafeTime;
        safeTime = 0f;  

        ref bool hasVisitedEnemySpawn = ref bot.HasVisitedEnemySpawn;
        hasVisitedEnemySpawn = true;
    }
}
