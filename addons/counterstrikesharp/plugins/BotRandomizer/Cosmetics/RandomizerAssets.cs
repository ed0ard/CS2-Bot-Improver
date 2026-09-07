namespace BotRandomizer;

internal static class RandomizerAssets
{
    internal const byte TerroristTeam = 2;
    internal const byte CounterTerroristTeam = 3;

    internal static readonly KnifeDefinition[] Knives =
    [
        // The four dominant types hold 70% together.
        new(515, 25), // Butterfly
        new(507, 21), // Karambit
        new(508, 17), // M9 Bayonet
        new(500, 7),  // Bayonet

        // The remaining 30% excludes near-zero types. Classic keeps 3%.
        new(525, 7), // Skeleton
        new(522, 6), // Stiletto
        new(523, 5), // Talon
        new(505, 4), // Flip
        new(509, 3), // Huntsman
        new(503, 3), // Classic
        new(519, 2)  // Ursus
    ];

    // Soft family weights keep the pool varied.
    internal static int GetGloveVariantWeight(ushort defIndex)
        => defIndex switch
        {
            5030 => 4, // Sport Gloves: 2x baseline
            5034 => 3, // Specialist Gloves: 1.5x baseline
            _ => 2
        };

    internal static int GetWeaponRarityWeight(CosmeticRarity rarity)
        => rarity switch
        {
            CosmeticRarity.Consumer => 1,
            CosmeticRarity.Industrial => 3,
            CosmeticRarity.MilSpec => 10,
            CosmeticRarity.Restricted => 22,
            CosmeticRarity.Classified => 23,
            CosmeticRarity.Covert => 40,
            CosmeticRarity.Contraband => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(rarity))
        };

    internal static int GetStickerFinishWeight(StickerFinish finish)
        => finish switch
        {
            StickerFinish.Paper => 43,
            StickerFinish.Glitter => 8,
            StickerFinish.Holo => 28,
            StickerFinish.Foil => 9,
            StickerFinish.Gold => 11,
            StickerFinish.Lenticular => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(finish))
        };

    // Four-sticker crafts favor deliberate Holo/Gold themes.
    internal static int GetFourRepeatStickerFinishWeight(StickerFinish finish)
        => finish switch
        {
            StickerFinish.Paper => 31,
            StickerFinish.Glitter => 11,
            StickerFinish.Holo => 40,
            StickerFinish.Foil => 7,
            StickerFinish.Gold => 11,
            StickerFinish.Lenticular => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(finish))
        };

    internal static int GetFourMixedStickerFinishWeight(StickerFinish finish)
        => finish switch
        {
            StickerFinish.Paper => 31,
            StickerFinish.Glitter => 5,
            StickerFinish.Holo => 22,
            StickerFinish.Foil => 8,
            StickerFinish.Gold => 34,
            StickerFinish.Lenticular => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(finish))
        };

    internal static readonly IReadOnlyDictionary<string, ushort> KnifeDefIndexByName =
        new Dictionary<string, ushort>
        {
            ["weapon_bayonet"] = 500,
            ["weapon_knife_css"] = 503,
            ["weapon_knife_flip"] = 505,
            ["weapon_knife_gut"] = 506,
            ["weapon_knife_karambit"] = 507,
            ["weapon_knife_m9_bayonet"] = 508,
            ["weapon_knife_tactical"] = 509,
            ["weapon_knife_falchion"] = 512,
            ["weapon_knife_survival_bowie"] = 514,
            ["weapon_knife_butterfly"] = 515,
            ["weapon_knife_push"] = 516,
            ["weapon_knife_cord"] = 517,
            ["weapon_knife_canis"] = 518,
            ["weapon_knife_ursus"] = 519,
            ["weapon_knife_gypsy_jackknife"] = 520,
            ["weapon_knife_outdoor"] = 521,
            ["weapon_knife_stiletto"] = 522,
            ["weapon_knife_widowmaker"] = 523,
            ["weapon_knife_skeleton"] = 525,
            ["weapon_knife_kukri"] = 526
        };

    // DefIndex 0 = default faction model. Team intro must not write AgentItem for those.
    internal static readonly AgentDefinition[] CounterTerroristAgents =
    [
        new(4757, "agents\\models\\ctm_diver\\ctm_diver_varianta.vmdl"),
        new(4771, "agents\\models\\ctm_diver\\ctm_diver_variantb.vmdl"),
        new(4772, "agents\\models\\ctm_diver\\ctm_diver_variantc.vmdl"),
        new(0, "agents\\models\\ctm_fbi\\ctm_fbi.vmdl"),
        new(0, "agents\\models\\ctm_fbi\\ctm_fbi_varianta.vmdl"),
        new(5308, "agents\\models\\ctm_fbi\\ctm_fbi_variantb.vmdl"),
        new(0, "agents\\models\\ctm_fbi\\ctm_fbi_variantc.vmdl"),
        new(0, "agents\\models\\ctm_fbi\\ctm_fbi_variantd.vmdl"),
        new(0, "agents\\models\\ctm_fbi\\ctm_fbi_variante.vmdl"),
        new(5305, "agents\\models\\ctm_fbi\\ctm_fbi_variantf.vmdl"),
        new(5306, "agents\\models\\ctm_fbi\\ctm_fbi_variantg.vmdl"),
        new(5307, "agents\\models\\ctm_fbi\\ctm_fbi_varianth.vmdl"),
        new(4749, "agents\\models\\ctm_gendarmerie\\ctm_gendarmerie_varianta.vmdl"),
        new(4750, "agents\\models\\ctm_gendarmerie\\ctm_gendarmerie_variantb.vmdl"),
        new(4751, "agents\\models\\ctm_gendarmerie\\ctm_gendarmerie_variantc.vmdl"),
        new(4752, "agents\\models\\ctm_gendarmerie\\ctm_gendarmerie_variantd.vmdl"),
        new(4753, "agents\\models\\ctm_gendarmerie\\ctm_gendarmerie_variante.vmdl"),
        new(0, "agents\\models\\ctm_sas\\ctm_sas.vmdl"),
        new(5601, "agents\\models\\ctm_sas\\ctm_sas_variantf.vmdl"),
        new(5602, "agents\\models\\ctm_sas\\ctm_sas_variantg.vmdl"),
        new(5401, "agents\\models\\ctm_st6\\ctm_st6_variante.vmdl"),
        new(5402, "agents\\models\\ctm_st6\\ctm_st6_variantg.vmdl"),
        new(5404, "agents\\models\\ctm_st6\\ctm_st6_varianti.vmdl"),
        new(4619, "agents\\models\\ctm_st6\\ctm_st6_variantj.vmdl"),
        new(5400, "agents\\models\\ctm_st6\\ctm_st6_variantk.vmdl"),
        new(4680, "agents\\models\\ctm_st6\\ctm_st6_variantl.vmdl"),
        new(5403, "agents\\models\\ctm_st6\\ctm_st6_variantm.vmdl"),
        new(5405, "agents\\models\\ctm_st6\\ctm_st6_variantn.vmdl"),
        new(4711, "agents\\models\\ctm_swat\\ctm_swat_variante.vmdl"),
        new(4712, "agents\\models\\ctm_swat\\ctm_swat_variantf.vmdl"),
        new(4713, "agents\\models\\ctm_swat\\ctm_swat_variantg.vmdl"),
        new(4714, "agents\\models\\ctm_swat\\ctm_swat_varianth.vmdl"),
        new(4715, "agents\\models\\ctm_swat\\ctm_swat_varianti.vmdl"),
        new(4716, "agents\\models\\ctm_swat\\ctm_swat_variantj.vmdl"),
        new(4756, "agents\\models\\ctm_swat\\ctm_swat_variantk.vmdl")
    ];

    internal static readonly AgentDefinition[] TerroristAgents =
    [
        new(5500, "agents\\models\\tm_balkan\\tm_balkan_variantf.vmdl"),
        new(5502, "agents\\models\\tm_balkan\\tm_balkan_variantg.vmdl"),
        new(5504, "agents\\models\\tm_balkan\\tm_balkan_varianth.vmdl"),
        new(5501, "agents\\models\\tm_balkan\\tm_balkan_varianti.vmdl"),
        new(5503, "agents\\models\\tm_balkan\\tm_balkan_variantj.vmdl"),
        new(4718, "agents\\models\\tm_balkan\\tm_balkan_variantk.vmdl"),
        new(5505, "agents\\models\\tm_balkan\\tm_balkan_variantl.vmdl"),
        new(4773, "agents\\models\\tm_jungle_raider\\tm_jungle_raider_varianta.vmdl"),
        new(4774, "agents\\models\\tm_jungle_raider\\tm_jungle_raider_variantb.vmdl"),
        new(4780, "agents\\models\\tm_jungle_raider\\tm_jungle_raider_variantb2.vmdl"),
        new(4775, "agents\\models\\tm_jungle_raider\\tm_jungle_raider_variantc.vmdl"),
        new(4776, "agents\\models\\tm_jungle_raider\\tm_jungle_raider_variantd.vmdl"),
        new(4777, "agents\\models\\tm_jungle_raider\\tm_jungle_raider_variante.vmdl"),
        new(4778, "agents\\models\\tm_jungle_raider\\tm_jungle_raider_variantf.vmdl"),
        new(4781, "agents\\models\\tm_jungle_raider\\tm_jungle_raider_variantf2.vmdl"),
        new(0, "agents\\models\\tm_leet\\tm_leet_varianta.vmdl"),
        new(0, "agents\\models\\tm_leet\\tm_leet_variantb.vmdl"),
        new(0, "agents\\models\\tm_leet\\tm_leet_variantc.vmdl"),
        new(0, "agents\\models\\tm_leet\\tm_leet_variantd.vmdl"),
        new(0, "agents\\models\\tm_leet\\tm_leet_variante.vmdl"),
        new(5108, "agents\\models\\tm_leet\\tm_leet_variantf.vmdl"),
        new(5105, "agents\\models\\tm_leet\\tm_leet_variantg.vmdl"),
        new(5106, "agents\\models\\tm_leet\\tm_leet_varianth.vmdl"),
        new(5107, "agents\\models\\tm_leet\\tm_leet_varianti.vmdl"),
        new(5109, "agents\\models\\tm_leet\\tm_leet_variantj.vmdl"),
        new(0, "agents\\models\\tm_phoenix\\tm_phoenix.vmdl"),
        new(0, "agents\\models\\tm_phoenix\\tm_phoenix_varianta.vmdl"),
        new(0, "agents\\models\\tm_phoenix\\tm_phoenix_variantb.vmdl"),
        new(0, "agents\\models\\tm_phoenix\\tm_phoenix_variantc.vmdl"),
        new(0, "agents\\models\\tm_phoenix\\tm_phoenix_variantd.vmdl"),
        new(5206, "agents\\models\\tm_phoenix\\tm_phoenix_variantf.vmdl"),
        new(5207, "agents\\models\\tm_phoenix\\tm_phoenix_variantg.vmdl"),
        new(5205, "agents\\models\\tm_phoenix\\tm_phoenix_varianth.vmdl"),
        new(5208, "agents\\models\\tm_phoenix\\tm_phoenix_varianti.vmdl"),
        new(4726, "agents\\models\\tm_professional\\tm_professional_varf.vmdl"),
        new(4733, "agents\\models\\tm_professional\\tm_professional_varf1.vmdl"),
        new(4734, "agents\\models\\tm_professional\\tm_professional_varf2.vmdl"),
        new(4735, "agents\\models\\tm_professional\\tm_professional_varf3.vmdl"),
        new(4736, "agents\\models\\tm_professional\\tm_professional_varf4.vmdl"),
        new(4613, "agents\\models\\tm_professional\\tm_professional_varf5.vmdl"),
        new(4727, "agents\\models\\tm_professional\\tm_professional_varg.vmdl"),
        new(4728, "agents\\models\\tm_professional\\tm_professional_varh.vmdl"),
        new(4732, "agents\\models\\tm_professional\\tm_professional_vari.vmdl"),
        new(4730, "agents\\models\\tm_professional\\tm_professional_varj.vmdl")
    ];
}
