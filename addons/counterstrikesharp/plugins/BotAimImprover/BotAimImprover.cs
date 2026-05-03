using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace BotAimImprover;

public class BotAimImprover : BasePlugin
{
    public override string ModuleName => "BotAimImprover";
    public override string ModuleVersion => "1.2.0";
    public override string ModuleAuthor => "ed0ard";
    public override string ModuleDescription => "Make bots aim better";

    private string _aimMode = "mixed";
    private CounterStrikeSharp.API.Modules.Timers.Timer? _mixedTimer;
    private bool _mixedPhase = true; // true = headshot phase

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnPlayerTakeDamagePre>(OnPlayerTakeDamagePre);
        
        AddCommand("bot_aim", "Set bot aim mode: head, body, mixed", OnBotAimCommand);
        
        // Default to mixed mode
        StartMixedMode();
    }

    public override void Unload(bool hotReload)
    {
        StopMixedMode();
        Server.ExecuteCommand("mp_damage_headshot_only 0");
    }

    private void StartMixedMode()
    {
        _mixedPhase = true;
        Server.ExecuteCommand("mp_damage_headshot_only 1");
        ScheduleMixedTick();
    }

    private void ScheduleMixedTick()
    {
        float delay = _mixedPhase ? 1.0f : 0.5f;
        _mixedTimer = AddTimer(delay, () =>
        {
            if (_aimMode != "mixed") return;
            _mixedPhase = !_mixedPhase;
            Server.ExecuteCommand(_mixedPhase ? "mp_damage_headshot_only 1" : "mp_damage_headshot_only 0");
            ScheduleMixedTick();
        });
    }

    private void StopMixedMode()
    {
        _mixedTimer?.Kill();
        _mixedTimer = null;
    }

    private void OnBotAimCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            Server.PrintToConsole($"[BotAimImprover] Current aim mode: {_aimMode}");
            return;
        }

        string arg = command.ArgByIndex(1).ToLower();
        if (arg != "head" && arg != "body" && arg != "mixed")
        {
            Server.PrintToConsole("[BotAimImprover] Valid values: head, body, mixed");
            return;
        }

        _aimMode = arg;
        StopMixedMode();

        switch (_aimMode)
        {
            case "head":
                Server.ExecuteCommand("mp_damage_headshot_only 1");
                break;
            case "body":
                Server.ExecuteCommand("mp_damage_headshot_only 0");
                break;
            case "mixed":
                StartMixedMode();
                break;
        }

        Server.PrintToConsole($"[BotAimImprover] Aim mode set to: {_aimMode}");
    }

    private HookResult OnPlayerTakeDamagePre(CCSPlayerPawn player, CTakeDamageInfo info)
    {
    if (player == null || !player.IsValid)
        return HookResult.Continue;

    var hitGroup = info.GetHitGroup();

    if (hitGroup != HitGroup_t.HITGROUP_HEAD &&
        (info.BitsDamageType & DamageTypes_t.DMG_BULLET) != 0)
    {
        info.BitsDamageType &= ~DamageTypes_t.DMG_BULLET;
        info.BitsDamageType |= DamageTypes_t.DMG_GENERIC;
    }

    return HookResult.Continue;
    }
}
