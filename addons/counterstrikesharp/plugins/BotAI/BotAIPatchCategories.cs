namespace BotAI;

/// <summary>
/// Groups every patch into a documented module category so configs can toggle
/// whole behaviors instead of individual signature names.
/// Every patch belongs to exactly one category; unknown platform-specific
/// names are ignored gracefully when resolving the disabled set.
/// </summary>
public static class BotAIPatchCategories
{
    public static readonly Dictionary<string, string[]> All = new(StringComparer.OrdinalIgnoreCase)
    {
        // Superhuman perception: unlimited vision cones and global hearing.
        ["Awareness"] =
        [
            "InViewCone_RemoveOuterFOV",
            "InViewCone_RemoveInnerFOV",
            "OnAudibleEvent_GlobalHearRange",
            "InvestigateNoise_SkipSelfDefenseCheck"
        ],

        // Global C4 intel: bots know bomb events from anywhere on the map.
        ["BombInfo"] =
        [
            "BombPickup_CT_GlobalHearRange",
            "BombBeep_CT_GlobalHearRange",
            "OnBombPlanted_AllBotsLearnSite"
        ],

        // Forced combat behavior: removes fire-rate limits and dodge dice rolls.
        ["CombatForce"] =
        [
            "AttackState_SkipFireRateCheck",
            "AttackState_SkipSteadyFireShortcut",
            "AttackState_SkipZoomFireShortcut",
            "SprayAllDistances_ForceHoldTrigger",
            "AttackState_DodgeChance100_Always",
            "AttackState_RetreatOnSniper_Disable",
            "AttackState_SkipSniperSpreadCheck"
        ],

        // Movement quality fixes: keeps vanilla randomness, unlocks abilities.
        ["Movement"] =
        [
            "AttackState_CanStrafe_jne",
            "AttackState_DodgeDuringReload",
            "SniperCrouchDodge_jb",
            "SniperDodge_SkipIsSniper_DodgeA",
            "AllSkill_KeepMoving_WhenSeeSniper",
            "LowSKill_JumpChance0"
        ],

        // Vision/attention enhancements: approach watching and noticing.
        ["VisionAttention"] =
        [
            "Vision_SkipIsMovingGate",
            "Vision_AlwaysEnterApproachBody_Cave",
            "Vision_AlwaysEnterApproachBody",
            "Vision_AlwaysWatchApproachPoints_Cave",
            "Vision_AlwaysWatchApproachPoints",
            "Vision_AlwaysWatchApproachPoints_LoopEntry_Cave",
            "Vision_AlwaysWatchApproachPoints_LoopEntry",
            "Vision_ApproachBody_SkipSkillCheck",
            "Vision_ApproachBody_SkipHidingSpotCheck",
            "IsNoticable_AlwaysTrue"
        ],

        // Bomb plant/defuse behavioral fixes (pure realism, no info cheats).
        ["BombBehavior"] =
        [
            "EscapeFromBomb_OnEnter_NoEquipKnife",
            "EscapeFromBomb_OnUpdate_NoEquipKnife",
            "EscapeFromFlames_OnEnter_NoEquipKnife",
            "PlantBombLookAtPriorityLow",
            "DefuseBombLookAtPriorityLow",
            "DefuseBomb_SkipIsVisibleCheck",
            "TBot_BombsiteSearch_UseKnownPlantedSite",
            "CT_Defuse_EngageAndInvestigate",
            "DefuseBombState_OnEnter_EngageAndInvestigate",
            "DefuseBombState_OnUpdate_EngageAndInvestigate"
        ],

        // Misc state machine fixes, incl. removing built-in flash avoidance.
        ["StateMachine"] =
        [
            "HasVisitedEnemySpawn",
            "GameState_Reset",
            "Idle_IsSafeAlwaysFalse",
            "FlashbangAvoidance_Disable",
            "Upkeep_BotCOS_ZeroDrift",
            "Upkeep_BotSIN_ZeroDrift"
        ]
    };
}
