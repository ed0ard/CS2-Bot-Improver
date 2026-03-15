using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace BotAimImprover;

public class BotAimImprover : BasePlugin
{
    public override string ModuleName => "BotAimImprover";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "ed0ard";
    public override string ModuleDescription => "Make bots aim better";

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnPlayerTakeDamagePre>(OnPlayerTakeDamagePre);
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