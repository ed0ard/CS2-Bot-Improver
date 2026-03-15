using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotRandomizer;

public class BotRandomizerPlugin : BasePlugin
{
    public override string ModuleName        => "BotRandomizer";
    public override string ModuleVersion     => "1.0.3";
    public override string ModuleAuthor      => "ed0ard";
    public override string ModuleDescription => "Randomize agent model and music kit for bots";

    private readonly Random _rng = new();
    private readonly Dictionary<int, string> _botModels = new();
    private readonly Dictionary<int, int> _botKits = new();
    private bool _handling = false;

    private static readonly string[] CtModels =
    {
        "characters\\models\\ctm_diver\\ctm_diver_varianta.vmdl",
        "characters\\models\\ctm_diver\\ctm_diver_variantb.vmdl",
        "characters\\models\\ctm_diver\\ctm_diver_variantc.vmdl",
        "characters\\models\\ctm_fbi\\ctm_fbi.vmdl",
        "characters\\models\\ctm_fbi\\ctm_fbi_varianta.vmdl",
        "characters\\models\\ctm_fbi\\ctm_fbi_variantb.vmdl",
        "characters\\models\\ctm_fbi\\ctm_fbi_variantc.vmdl",
        "characters\\models\\ctm_fbi\\ctm_fbi_variantd.vmdl",
        "characters\\models\\ctm_fbi\\ctm_fbi_variante.vmdl",
        "characters\\models\\ctm_fbi\\ctm_fbi_variantf.vmdl",
        "characters\\models\\ctm_fbi\\ctm_fbi_variantg.vmdl",
        "characters\\models\\ctm_fbi\\ctm_fbi_varianth.vmdl",
        "characters\\models\\ctm_gendarmerie\\ctm_gendarmerie_varianta.vmdl",
        "characters\\models\\ctm_gendarmerie\\ctm_gendarmerie_variantb.vmdl",
        "characters\\models\\ctm_gendarmerie\\ctm_gendarmerie_variantc.vmdl",
        "characters\\models\\ctm_gendarmerie\\ctm_gendarmerie_variantd.vmdl",
        "characters\\models\\ctm_gendarmerie\\ctm_gendarmerie_variante.vmdl",
        "characters\\models\\ctm_sas\\ctm_sas.vmdl",
        "characters\\models\\ctm_sas\\ctm_sas_variantf.vmdl",
        "characters\\models\\ctm_sas\\ctm_sas_variantg.vmdl",
        "characters\\models\\ctm_st6\\ctm_st6_variante.vmdl",
        "characters\\models\\ctm_st6\\ctm_st6_variantg.vmdl",
        "characters\\models\\ctm_st6\\ctm_st6_varianti.vmdl",
        "characters\\models\\ctm_st6\\ctm_st6_variantj.vmdl",
        "characters\\models\\ctm_st6\\ctm_st6_variantk.vmdl",
        "characters\\models\\ctm_st6\\ctm_st6_variantl.vmdl",
        "characters\\models\\ctm_st6\\ctm_st6_variantm.vmdl",
        "characters\\models\\ctm_st6\\ctm_st6_variantn.vmdl",
        "characters\\models\\ctm_swat\\ctm_swat_variante.vmdl",
        "characters\\models\\ctm_swat\\ctm_swat_variantf.vmdl",
        "characters\\models\\ctm_swat\\ctm_swat_variantg.vmdl",
        "characters\\models\\ctm_swat\\ctm_swat_varianth.vmdl",
        "characters\\models\\ctm_swat\\ctm_swat_varianti.vmdl",
        "characters\\models\\ctm_swat\\ctm_swat_variantj.vmdl",
        "characters\\models\\ctm_swat\\ctm_swat_variantk.vmdl",
    };

    private static readonly string[] TModels =
    {
        "characters\\models\\tm_balkan\\tm_balkan_variantf.vmdl",
        "characters\\models\\tm_balkan\\tm_balkan_variantg.vmdl",
        "characters\\models\\tm_balkan\\tm_balkan_varianth.vmdl",
        "characters\\models\\tm_balkan\\tm_balkan_varianti.vmdl",
        "characters\\models\\tm_balkan\\tm_balkan_variantj.vmdl",
        "characters\\models\\tm_balkan\\tm_balkan_variantk.vmdl",
        "characters\\models\\tm_balkan\\tm_balkan_variantl.vmdl",
        "characters\\models\\tm_jumpsuit\\tm_jumpsuit_varianta.vmdl",
        "characters\\models\\tm_jumpsuit\\tm_jumpsuit_variantb.vmdl",
        "characters\\models\\tm_jumpsuit\\tm_jumpsuit_variantc.vmdl",
        "characters\\models\\tm_jungle_raider\\tm_jungle_raider_varianta.vmdl",
        "characters\\models\\tm_jungle_raider\\tm_jungle_raider_variantb.vmdl",
        "characters\\models\\tm_jungle_raider\\tm_jungle_raider_variantb2.vmdl",
        "characters\\models\\tm_jungle_raider\\tm_jungle_raider_variantc.vmdl",
        "characters\\models\\tm_jungle_raider\\tm_jungle_raider_variantd.vmdl",
        "characters\\models\\tm_jungle_raider\\tm_jungle_raider_variante.vmdl",
        "characters\\models\\tm_jungle_raider\\tm_jungle_raider_variantf.vmdl",
        "characters\\models\\tm_jungle_raider\\tm_jungle_raider_variantf2.vmdl",
        "characters\\models\\tm_leet\\tm_leet_varianta.vmdl",
        "characters\\models\\tm_leet\\tm_leet_variantb.vmdl",
        "characters\\models\\tm_leet\\tm_leet_variantc.vmdl",
        "characters\\models\\tm_leet\\tm_leet_variantd.vmdl",
        "characters\\models\\tm_leet\\tm_leet_variante.vmdl",
        "characters\\models\\tm_leet\\tm_leet_variantf.vmdl",
        "characters\\models\\tm_leet\\tm_leet_variantg.vmdl",
        "characters\\models\\tm_leet\\tm_leet_varianth.vmdl",
        "characters\\models\\tm_leet\\tm_leet_varianti.vmdl",
        "characters\\models\\tm_leet\\tm_leet_variantj.vmdl",
        "characters\\models\\tm_phoenix\\tm_phoenix.vmdl",
        "characters\\models\\tm_phoenix\\tm_phoenix_varianta.vmdl",
        "characters\\models\\tm_phoenix\\tm_phoenix_variantb.vmdl",
        "characters\\models\\tm_phoenix\\tm_phoenix_variantc.vmdl",
        "characters\\models\\tm_phoenix\\tm_phoenix_variantd.vmdl",
        "characters\\models\\tm_phoenix\\tm_phoenix_variantf.vmdl",
        "characters\\models\\tm_phoenix\\tm_phoenix_variantg.vmdl",
        "characters\\models\\tm_phoenix\\tm_phoenix_varianth.vmdl",
        "characters\\models\\tm_phoenix\\tm_phoenix_varianti.vmdl",
        "characters\\models\\tm_professional\\tm_professional_varf.vmdl",
        "characters\\models\\tm_professional\\tm_professional_varf1.vmdl",
        "characters\\models\\tm_professional\\tm_professional_varf2.vmdl",
        "characters\\models\\tm_professional\\tm_professional_varf3.vmdl",
        "characters\\models\\tm_professional\\tm_professional_varf4.vmdl",
        "characters\\models\\tm_professional\\tm_professional_varf5.vmdl",
        "characters\\models\\tm_professional\\tm_professional_varg.vmdl",
        "characters\\models\\tm_professional\\tm_professional_varh.vmdl",
        "characters\\models\\tm_professional\\tm_professional_vari.vmdl",
        "characters\\models\\tm_professional\\tm_professional_varj.vmdl",
    };

    private static readonly int[] KitIds =
    {
         1,  2,  3,  4,  5,  6,  7,  8,  9, 10,
        11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
        21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
        31, 39, 40, 41, 42, 43, 44, 45, 46, 47,
        48, 49, 50, 51, 52, 53, 54, 55, 56, 57,
        58, 59, 60, 61, 62, 63, 64, 65, 66, 67,
        68, 69, 70, 71, 72, 73, 74, 75, 76, 78,
        79, 80, 81, 82, 83, 84, 85, 86, 87, 88,
        89, 90, 91, 92, 93, 94, 95, 96,
    };

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _botModels.Clear();
            _botKits.Clear();
            foreach (var m in CtModels) Server.PrecacheModel(m);
            foreach (var m in TModels)  Server.PrecacheModel(m);
        });

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundMvp>(OnRoundMvp, HookMode.Pre);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player == null
            || !player.IsValid
            || !player.IsBot
            || player.PlayerPawn == null
            || !player.PlayerPawn.IsValid
            || player.PlayerPawn.Value == null
            || !player.PlayerPawn.Value.IsValid)
            return HookResult.Continue;

        if ((CsTeam)player.TeamNum != CsTeam.CounterTerrorist
            && (CsTeam)player.TeamNum != CsTeam.Terrorist)
            return HookResult.Continue;

        if (!_botModels.TryGetValue(player.Slot, out string? model))
        {
            string[] pool = (CsTeam)player.TeamNum == CsTeam.CounterTerrorist ? CtModels : TModels;
            model = pool[_rng.Next(pool.Length)];
            _botModels[player.Slot] = model;
        }

        if (!_botKits.ContainsKey(player.Slot))
            _botKits[player.Slot] = KitIds[_rng.Next(KitIds.Length)];

        var pawn          = player.PlayerPawn.Value;
        var assignedModel = model;
        var kitId         = _botKits[player.Slot];

        Server.NextFrame(() =>
        {
            if (pawn == null || !pawn.IsValid) return;
            pawn.SetModel(assignedModel);
            var c = pawn.Render;
            pawn.Render = Color.FromArgb(255, c.R, c.G, c.B);
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");

            if (player == null || !player.IsValid) return;
            player.MusicKitID = kitId;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMusicKitID");
        });

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot)
            return HookResult.Continue;

        _botModels.Remove(player.Slot);
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        if (_handling)
            return HookResult.Continue;

        var player = @event.Userid;

        if (player == null || !player.IsValid || !player.IsBot)
            return HookResult.Continue;

        if (!_botKits.TryGetValue(player.Slot, out int kitId))
            return HookResult.Continue;

        info.DontBroadcast = true;
        _handling = true;

        if (player.MusicKitID != kitId)
        {
            player.MusicKitID = kitId;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMusicKitID");
        }

        EventRoundMvp? newEvent = null;
        try
        {
            newEvent = new EventRoundMvp(true)
            {
                Userid     = player,
                Musickitid = kitId,
                Nomusic    = 0,
                Reason     = @event.Reason,
                Value      = @event.Value,
            };

            foreach (var human in Utilities.GetPlayers()
                         .Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false }))
            {
                try { newEvent.FireEventToClient(human); }
                catch { }
            }
        }
        finally
        {
            try { newEvent?.Free(); } catch { }
            _handling = false;
        }

        return HookResult.Continue;
    }
}
