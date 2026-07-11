using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using Common;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace BotAI;

public record PatchInfo(string Name, nint Address, List<byte> OriginalBytes);

public static class BotOffsets
{
    public const int m_gameState   = 0x6038;
    public const int m_isRoundOver = 0x08;
    public const int m_bombState   = 0x0C;
}

[MinimumApiVersion(304)]
public class BotAI : BasePlugin
{
    public override string ModuleName        => "Patches - Bot AI";
    public override string ModuleVersion     => "1.8.4";
    public override string ModuleAuthor      => "K4ryuu & Austin (updated by ed0ard & Misaka17032)";
    public override string ModuleDescription =>
        "Improve and fix bots' behavior comprehensively";

    private readonly List<PatchInfo> _appliedPatches = [];
    private readonly bool _isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private readonly Dictionary<string, (string signature, string patch, string expectedOriginal, int patchOffset)>
        _patchDefinitions = new()
    {

        // Force HasVisitedEnemySpawn = 1 so bots don't revisit enemy spawn
        ["HasVisitedEnemySpawn"] = (
            signature:        "40 88 B7 20 05 00 00",
            patch:            "C6 87 20 05 00 00 01",
            expectedOriginal: "40 88 B7 20 05 00 00",
            patchOffset:      0
        ),

        // NOP the BombState reset to avoid bot confusion
        ["GameState_Reset"] = (
            signature:        "83 7F 0C 00 74 07 C7 47 0C 00 00 00 00",
            patch:            "0F 1F 80 00 00 00 00",
            expectedOriginal: "C7 47 0C 00 00 00 00",
            patchOffset:      6
        ),

        // IsSafe() always false in IdleState → bots don't idle near safe areas
        ["Idle_IsSafeAlwaysFalse"] = (
            signature:        "74 28 33 D2 48 8B CE E8 ? ? ? ? 84 C0 75 1A",
            patch:            "EB 28",
            expectedOriginal: "74 28",
            patchOffset:      0
        ),


        // EscapeFromBombState::OnEnter tail-call jmp → ret (prevents crash)
        ["EscapeFromBomb_OnEnter_NoEquipKnife"] = (
            signature:        "C6 83 ? ? 00 00 00 C6 83 ? ? 00 00 00 48 83 C4 20 5B E9",
            patch:            "C3 90 90 90 90",
            expectedOriginal: "E9 ? ? ? ?",
            patchOffset:      19
        ),

        // EscapeFromBombState::OnUpdate call → NOP
        ["EscapeFromBomb_OnUpdate_NoEquipKnife"] = (
            signature:        "75 0F 48 8B 5C 24 50 48 83 C4 40 5F E9 ? ? ? ? E8 ? ? ? ?",
            patch:            "90 90 90 90 90",
            expectedOriginal: "E8 ? ? ? ?",
            patchOffset:      17
        ),

        // EscapeFromFlamesState::OnEnter call → NOP
        ["EscapeFromFlames_OnEnter_NoEquipKnife"] = (
            signature:        "48 8B CB 40 88 BB ? ? 00 00 40 88 BB ? ? 00 00 E8 ? ? ? ? F3 0F 10 0D",
            patch:            "90 90 90 90 90",
            expectedOriginal: "E8 ? ? ? ?",
            patchOffset:      17
        ),


        ["PlantBombLookAtPriorityLow"] = (
            signature:        "41 B9 02 00 00 00 C6 44 24 38 00 F3 0F 10 0D",
            patch:            "41 B9 00 00 00 00",
            expectedOriginal: "41 B9 02 00 00 00",
            patchOffset:      0    // VA 0x18031ae2c
        ),

        ["DefuseBombLookAtPriorityLow"] = (
            signature:        "41 B9 02 00 00 00 C6 44 24 38 00 4C 8B C7",
            patch:            "41 B9 00 00 00 00",
            expectedOriginal: "41 B9 02 00 00 00",
            patchOffset:      0    // VA 0x18031cce6
        ),

        // MoveToState::OnUpdate - DefuseBomb IsVisible gate removal
        // Source: if (me->IsVisible(*bombPos)) { me->DefuseBomb(); }
        // Patch: NOP the je that skips DefuseBomb when IsVisible returns false.
        // The two preceding distance checks (72u 3D + 48u 2D) remain intact.
        ["DefuseBomb_SkipIsVisibleCheck"] = (
            signature:        "0F 2F C8 0F 86 ? ? ? ? 45 33 C9 45 33 C0 48 8B D3 48 8B CE E8 ? ? ? ? 84 C0 0F 84 ? ? ? ? 48 8B CE",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 84 D9 00 00 00",
            patchOffset:      28
        ),

        ["AttackState_SkipFireRateCheck"] = (
            signature:        "0F 2F 8B ? ? 00 00 0F 82",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 82 87 00 00 00",
            patchOffset:      7    // VA 0x1802f22a0
        ),

        // Force bot to hold the trigger at all ranges & all weapons.
        // At the trigger gate, the release-between-shots flag bpl is set to 1 (tap/burst)
        // when fire delay > one tick. Rewrite "mov bpl,1" -> "xor bpl,bpl" so bpl is
        // always 0 (hold trigger) -> continuous spray everywhere; recoil control follows.
        ["SprayAllDistances_ForceHoldTrigger"] = (
            signature:        "76 12 48 8B 05 ? ? ? ? 0F 2F 40 30 76 05 40 B5 01 EB 03 40 32 ED",
            patch:            "40 32 ED",
            expectedOriginal: "40 B5 01",
            patchOffset:      15
        ),

        ["AttackState_SkipSteadyFireShortcut"] = (
            signature:        "0F B6 F0 84 C0 74 3C 48 8B 4B 18 48 8B 11 FF 92 90 00 00 00",
            patch:            "90 90",
            expectedOriginal: "74 3C",
            patchOffset:      5    // RVA 0x2f1be5: je+3C → NOP (remove HasViewBeenSteady fire shortcut)
        ),
 
        ["AttackState_SkipZoomFireShortcut"] = (
            signature:        "FF 90 ? ? 00 00 84 C0 74 15 48 8D 8B ? ? 00 00 48 89 AB",
            patch:            "90 90",
            expectedOriginal: "74 15",
            patchOffset:      8    // RVA 0x2f1c0c: je+15 → NOP (remove IsWaitingForZoom fire shortcut)
        ),

        ["AttackState_SkipSniperSpreadCheck"] = (
            signature:        "41 0F 28 C8 0F 57 C0 FF 15 ? ? ? ? F3 0F 10 0D ? ? ? ? 0F 2F C8 0F 86",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 86 81 04 00 00",
            patchOffset:      24  // RVA 0x320153: NOP jbe+47B
        ),


        ["AttackState_DodgeDuringReload"] = (
            signature:        "E9 ? ? ? ? 0F 2F BB ? 00 00 00 76 74",
            patch:            "EB 74",
            expectedOriginal: "76 74",
            patchOffset:      12    // BLOCK_TIMER_A jbe→jmp
        ),

        ["SniperCrouchDodge_jb"] = (
            signature:        "0F 2F BB ? 00 00 00 0F 28 7C 24 30 76 74",
            patch:            "90 90",
            expectedOriginal: "76 74",
            patchOffset:      12    // BLOCK_TIMER_B NOP jbe → DODGE_B (RVA 0x2f2420)
        ),

        ["LowSKill_JumpChance0"] = (
            signature:        "FF 90 90 00 00 00 0F 2F 05 ? ? ? ? 76 11",
            patch:            "EB 40",
            expectedOriginal: "76 11",
            patchOffset:      13    // RVA 0x2f4587: jbe +11 → jmp +40 to non-jump 
        ),

        // Source: AttackState::OnEnter
        // skill>0.5 && (Outnumbered || CanSeeSniper) → dodgeChance=100
        ["AttackState_DodgeChance100_Always"] = (
            signature:        "0F 28 F0 F3 0F 59 35 ? ? ? ? 76 15",
            patch:            "EB 11",
            expectedOriginal: "76 15",
            patchOffset:      11
        ),

        // Source: AttackState::OnUpdate
        // (CanSeeSniper && !IsSniper) → retreat
        ["AttackState_RetreatOnSniper_Disable"] = (
            signature:        "44 38 B6 ? 5C 00 00 74 0C 48 8B CE E8 ? ? ? ? 84 C0",
            patch:            "EB 0C",
            expectedOriginal: "74 0C",
            patchOffset:      7
        ),

        ["AllSkill_KeepMoving_WhenSeeSniper"] = (
            signature:        "0F 2F 05 ? ? ? ? 76 0D 80 BF ? ? 00 00 00 0F 85",
            patch:            "90 90",
            expectedOriginal: "76 0D",
            patchOffset:      7    // RVA 0x2cbb4d: jbe +0D → NOP
        ),

        ["AttackState_CanStrafe_jne"] = (
            signature:        "48 8B CB E8 ? ? ? ? 84 C0 74 7B",
            patch:            "90 90",
            expectedOriginal: "74 7B",
            patchOffset:      10    // RVA 0x2f22b0
        ),

        ["SniperDodge_SkipIsSniper_DodgeA"] = (
            signature:        "84 F6 75 6A 48 8B 05",
            patch:            "90 90",
            expectedOriginal: "75 6A",
            patchOffset:      2    // RVA 0x2f23a8：DODGE_A IsSniper jne+6A → NOP
        ),


        ["Vision_AlwaysWatchApproachPoints"] = (
            signature:        "80 BF ? ? 00 00 00 75 25 0F 2F",
            patch:            "EB 25",
            expectedOriginal: "75 25",
            patchOffset:      7    // VA 0x180319304: jne→jmp
        ),

        ["Vision_ApproachBody_SkipSkillCheck"] = (
            signature:        "0F 2F C6 76 33 80 BF ? ? 00 00 00 74 2A",
            patch:            "90 90",
            expectedOriginal: "76 33",
            patchOffset:      3
        ),

        ["Vision_ApproachBody_SkipHidingSpotCheck"] = (
            signature:        "0F 2F C6 76 33 80 BF ? ? 00 00 00 74 2A",
            patch:            "90 90",
            expectedOriginal: "74 2A",
            patchOffset:      12
        ),

        ["Vision_SkipIsMovingGate"] = (
            signature:        "0F 2F 35 ? ? ? ? 77 0F 49 8B D6 48 8B CF E8",
            patch:            "90 90",
            expectedOriginal: "77 0F",
            patchOffset:      7    //RVA 0x319306: ja → NOP
        ),

        ["Vision_AlwaysEnterApproachBody"] = (
            signature:        "84 C0 75 0D 48 C7 45 08 00 00 00 00 E9",
            patch:            "EB 0D",
            expectedOriginal: "75 0D",
            patchOffset:      2    //RVA 0x31931c: jne → jmp
        ),

        // IsNoticable（raw 0x2DA930）
        ["IsNoticable_AlwaysTrue"] = (
            signature:        "40 53 48 83 EC 30 48 8B D9 BA FF FF FF FF 48 8D 0D ? ? ? ? E8 ? ? ? ? 48 85 C0 75",
            patch:            "B0 01 C3",
            expectedOriginal: "40 53 48",
            patchOffset:      0
        ),

        // InViewCone(bot, target):
        //      angle = GetFOVToPosition(target) 
        //      if angle > 60.0f:
        //          return 0 
        //      eax = 0
        //      angle2 = GetFOVToPosition(target)
        //      eax = (angle2 <= 25.0f) ? 1 : 0
        //      eax += 1
        //      return eax   
        ["InViewCone_RemoveOuterFOV"] = (
            signature:        "FF 90 E0 00 00 00 0F 2F 05 ? ? ? ? 76 08 33 C0",
            patch:            "90 90",
            expectedOriginal: "76 08",
            patchOffset:      13
        ),

        ["InViewCone_RemoveInnerFOV"] = (
            signature:        "0F 96 C0 FF C0 48 83 C4 20 5B C3",
            patch:            "B0 01 90",
            expectedOriginal: "0F 96 C0",
            patchOffset:      0
        ),


        ["InvestigateNoise_SkipSelfDefenseCheck"] = (
            signature:        "83 BB ? ? 00 00 02 74 1E",
            patch:            "90 90",
            expectedOriginal: "74 1E",
            patchOffset:      7     // VA 0x180335696: je → NOP NOP
        ),

        // Source: IdleState::OnUpdate, T-side "bomb planted but site unknown" path
        //   bombSite = GetGameState()->GetNextBombsiteToSearch();
        //   → replaced with:
        //   bombSite = GetGameState()->GetPlantedBombsite();
        // With the patch active: [gameState+0x68] is set at plant time for all bots.
        //  directly to the planted site instead of random searching.
        ["TBot_BombsiteSearch_UseKnownPlantedSite"] = (
            signature:        "48 8B 8E ? ? 00 00 E8 ? ? ? ? 48 8B CB E8 ? ? ? ? 4C 8B 05 ? ? ? ? 85 C0",
            patch:            "E8 28 41 F9 FF",
            expectedOriginal: "E8 38 3B F9 FF",
            patchOffset:      15
        ),

        // Source: cs_bot_event_bomb / OnBombBeep handler
        //   const float bombBeepHearRangeSq = 1500.0f * 1500.0f;
        //   if (rangeSq > bombBeepHearRangeSq) return;
        // NOP the jbe → CT bots always enter the bombsite-update path,
        // regardless of distance to the bomb.
        ["BombBeep_CT_GlobalHearRange"] = (
            signature:        "F3 0F 59 C9 F3 0F 59 C0 F3 0F 59 F6 F3 0F 58 C8 F3 0F 10 05 ? ? ? ? F3 0F 58 CE 0F 2F C1 76 67",
            patch:            "90 90",
            expectedOriginal: "76 67",
            patchOffset:      31
        ),

        // Source: cs_bot_event_bomb.cpp — OnBombPickedUp
        //   const float bombPickupHearRangeSq = 1000.0f * 1000.0f;
        //   if (LengthSqr() < bombPickupHearRangeSq) → CT tracks bomber
        // NOP jbe → all CT bots always track who picks up the bomb.
        ["BombPickup_CT_GlobalHearRange"] = (
            signature:        "F3 0F 59 D2 F3 0F 59 C0 F3 0F 59 C9 F3 0F 58 D1 F3 0F 58 D0 F3 0F 10 05 ? ? ? ? 0F 2F C2 76 23",
            patch:            "90 90",
            expectedOriginal: "76 23",
            patchOffset:      31
        ),

        // Source: CCSBot::OnAudibleEvent — universal sound event gate
        //   if (newNoiseDist < range) → heard
        // All sound events (weapon_fire, footsteps, reload, grenade bounce,
        //   door, flashbang, etc.) funnel through OnAudibleEvent.
        // NOP the jbe (6 bytes: 0F 86 → 90 90 90 90 90 90) → every bot
        // hears every sound event regardless of distance. This replaces the
        // need for individual per-event patches
        ["OnAudibleEvent_GlobalHearRange"] = (
            signature:        "F3 44 0F 51 CA EB 0C 0F 28 C2 E8 ? ? ? ? 44 0F 28 C8 45 0F 2F D1 0F 86 ? ? ? ?",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 86 9E 03 00 00",
            patchOffset:      23
        ),
   
        // Source: CSGameState::OnBombPlanted (cs_gamestate)
        //   // Terrorists always know where the bomb is
        //   if (m_owner->GetTeamNumber() == TEAM_TERRORIST && plantingPlayer)
        //       UpdatePlantedBomb(plantingPlayer->GetAbsOrigin());
        // NOP the jne (75 6D → 90 90) → ALL bots get
        ["OnBombPlanted_AllBotsLearnSite"] = (
            signature:        "80 BA ? ? 00 00 02 75 6D 48 85 DB 74 68",
            patch:            "90 90",
            expectedOriginal: "75 6D",
            patchOffset:      7
        ), 

        // Source: cs_bot_defuse_bomb.cpp — DefuseBombState enter
        //   me->SetDisposition(SELF_DEFENSE);  ← suppresses investigate-noise,
        //   prevents the bot from reacting to anything except direct threats.
        //   SELF_DEFENSE=2, ENGAGE_AND_INVESTIGATE=0, OPPORTUNITY_FIRE=1
        // Patch edx=2 → edx=0 (ENGAGE_AND_INVESTIGATE) so the defusing CT
        // will chase noises and actively hunt while moving to defuse.
        ["CT_Defuse_EngageAndInvestigate"] = (
            signature:        "C7 86 ? ? 00 00 03 00 00 00 BA 02 00 00 00 48 8B CE",
            patch:            "BA 00 00 00 00",
            expectedOriginal: "BA 02 00 00 00",
            patchOffset:      10
        ),

        // Source: cs_bot_defuse_bomb.cpp — DefuseBombState::OnUpdate
        //   me->SetDisposition(CCSBot::SELF_DEFENSE); 
        // mov edx, 2 (SELF_DEFENSE)-> 0 (ENGAGE_AND_INVESTIGATE)
        ["DefuseBombState_OnUpdate_EngageAndInvestigate"] = (
            signature:        "48 8D 8A ? ? 00 00 48 8B DA E8 ? ? ? ? BA 02 00 00 00 48 8B CB",
            patch:            "BA 00 00 00 00",
            expectedOriginal: "BA 02 00 00 00",
            patchOffset:      15
        ),

        // Source: cs_bot_defuse_bomb.cpp — DefuseBombState::OnEnter
        //   me->SetDisposition(CCSBot::SELF_DEFENSE);
        // SetDisposition call at RVA 0x334CC5: edx=2 (SELF_DEFENSE)
        // patch edx=2 → edx=0 (ENGAGE_AND_INVESTIGATE)
        ["DefuseBombState_OnEnter_EngageAndInvestigate"] = (
            signature:        "48 89 5C 24 08 57 48 83 EC 20 48 8B DA BA 02 00 00 00 48 8B CB",
            patch:            "BA 00 00 00 00",
            expectedOriginal: "BA 02 00 00 00",
            patchOffset:      13
        ),


        // Source: cs_bot_weapon.cpp — CheckGrenadeDanger(), flash avoidance block
        //   m_me->ClearLookAt();     ← KEPT (writes to bot state fields, harmless)
        //   m_me->SetLookAt("Avoid Flashbang", away, PRIORITY_UNINTERRUPTABLE, duration);  ← PATCHED OUT
        //   m_me->StopAiming();      ← PATCHED OUT
        //   return false;            ← KEPT (return value unchanged, avoid crash)
        ["FlashbangAvoidance_Disable"] = (
            signature:        "F3 0F 10 05 ? ? ? ? F3 0F 11 44 24 30 88 4C 24 28 49 8B 0E F3 0F 11 4C 24 20 E8 ? ? ? ? 49 8B 06 C6 80 7C 5C 00 00 00",
            patch:            "90 90 90 90 90 90 90 90 90 90 90 90 90 90 90",
            expectedOriginal: "E8 5C A4 02 00 49 8B 06 C6 80 7C 5C 00 00 00",
            patchOffset:      27
        ),
    };

    private readonly Dictionary<string, (string signature, string patch, string expectedOriginal, int patchOffset)>
        _patchDefinitionsLinux = new()
    {
        // Confirmed against /home/misaka/cs2/game/csgo/bin/linuxsteamrt64/libserver.so (2026-06-09).
        // Force HasVisitedEnemySpawn = 1 so bots don't revisit enemy spawn.
        ["HasVisitedEnemySpawn"] = (
            signature:        "40 88 B7 6C 07 00 00 C3",
            patch:            "C6 87 6C 07 00 00 01",
            expectedOriginal: "40 88 B7 6C 07 00 00",
            patchOffset:      0
        ),

        // NOP the BombState reset in CSGameState::Reset() (linux-specific bytes).
        ["GameState_Reset"] = (
            signature:        "C6 47 08 00 48 89 07 48 C7 47 0C 00 00 00 00 C7 47 14 00 00 00 00 C3",
            patch:            "0F 1F 84 00 00 00 00 00",
            expectedOriginal: "48 C7 47 0C 00 00 00 00",
            patchOffset:      7
        ),

        // IdleState::OnUpdate: treat IsSafe() as false so bots don't idle near safe areas.
        ["Idle_IsSafeAlwaysFalse"] = (
            signature:        "48 89 DF E8 ? ? ? ? 84 C0 75 B3 48 8B 5D F8 C9 C3",
            patch:            "90 90",
            expectedOriginal: "75 B3",
            patchOffset:      10
        ),

        // EscapeFromBombState::OnEnter tail-call to EquipKnife() -> ret.
        ["EscapeFromBomb_OnEnter_NoEquipKnife"] = (
            signature:        "C6 83 84 4F 00 00 00 48 8B 5D F8 C9 E9 ? ? ? ?",
            patch:            "C3 90 90 90 90",
            expectedOriginal: "E9 ? ? ? ?",
            patchOffset:      12
        ),

        // EscapeFromBombState::OnUpdate call to EquipKnife() -> NOP.
        ["EscapeFromBomb_OnUpdate_NoEquipKnife"] = (
            signature:        "48 85 C0 0F 84 ? ? ? ? 48 89 DF 49 89 C4 E8 ? ? ? ? 31 F6 48 89 DF E8 ? ? ? ?",
            patch:            "90 90 90 90 90",
            expectedOriginal: "E8 ? ? ? ?",
            patchOffset:      15
        ),

        // EscapeFromFlamesState::OnEnter call to EquipKnife() -> NOP.
        ["EscapeFromFlames_OnEnter_NoEquipKnife"] = (
            signature:        "C6 83 5C 4F 00 00 00 48 89 DF C6 83 84 4F 00 00 00 E8 ? ? ? ? F3 0F 10 1D",
            patch:            "90 90 90 90 90",
            expectedOriginal: "E8 ? ? ? ?",
            patchOffset:      17
        ),

        ["PlantBombLookAtPriorityLow"] = (
            signature:        "48 8D 55 C8 4C 89 E7 45 31 C9 F3 0F 10 40 08 45 31 C0 B9 02 00 00 00 48 89 5D C8 F3 0F 10 0D ? ? ? ? 48 8D 35 ? ? ? ? F3 0F 11 45 D0",
            patch:            "B9 00 00 00 00",
            expectedOriginal: "B9 02 00 00 00",
            patchOffset:      18
        ),

        ["DefuseBombLookAtPriorityLow"] = (
            signature:        "4C 89 E2 45 31 C9 45 31 C0 F3 0F 10 05 ? ? ? ? B9 02 00 00 00 48 89 DF 48 8D 35 ? ? ? ? E8 ? ? ? ?",
            patch:            "B9 00 00 00 00",
            expectedOriginal: "B9 02 00 00 00",
            patchOffset:      17
        ),

        // MoveToState::OnUpdate - DefuseBomb IsVisible gate removal.
        ["DefuseBomb_SkipIsVisibleCheck"] = (
            signature:        "0F 2F C8 0F 86 ? ? ? ? 31 C9 31 D2 4C 89 E6 48 89 DF E8 ? ? ? ? 84 C0 0F 84 ? ? ? ? 48 83 C4 78 48 89 DF",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 84 ? ? ? ?",
            patchOffset:      26
        ),

        // Skip fire-rate check in AttackState::Update for low-latency patch.
        ["AttackState_SkipFireRateCheck"] = (
            signature:        "0F 2F 8B ? ? 00 00 0F 82 42 FF FF FF",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 82 ? ? ? ?",
            patchOffset:      7
        ),

        // AttackState::OnUpdate: keep the fire shortcut from returning early.
        ["AttackState_SkipSteadyFireShortcut"] = (
            signature:        "BA 01 00 00 00 48 89 DF 48 89 C6 E8 ? ? ? ? 84 C0 0F 84 ? ? ? ? 48 89 DF E8 ? ? ? ?",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 84 ? ? ? ?",
            patchOffset:      18
        ),

        // AttackState::OnUpdate: don't leave the zoom/lineup shortcut path early.
        ["AttackState_SkipZoomFireShortcut"] = (
            signature:        "F3 0F 10 05 ? ? ? ? 48 89 DF E8 ? ? ? ? 84 C0 0F 84 ? ? ? ? 83 BB C8 05 00 00 14",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 84 ? ? ? ?",
            patchOffset:      18
        ),

        // AttackState::OnUpdate: force the continuous-fire flag before SetLookAt().
        ["SprayAllDistances_ForceHoldTrigger"] = (
            signature:        "F3 0F 10 45 90 48 83 C4 68 4C 89 FE 48 89 DF 5B BA 01 00 00 00 41 5C 41 5D 41 5E 41 5F 5D E9 ? ? ? ?",
            patch:            "31 D2 90 90 90",
            expectedOriginal: "BA 01 00 00 00",
            patchOffset:      16
        ),

        // AttackState::OnEnter: always take the high-skill dodge chance path.
        ["AttackState_DodgeChance100_Always"] = (
            signature:        "48 89 DF F3 0F 11 85 58 FE FF FF E8 ? ? ? ? 84 C0 0F 84 ? ? ? ? 44 8B 2D ? ? ? ? 45 89 EE",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 84 ? ? ? ?",
            patchOffset:      18
        ),

        // AttackState::OnUpdate: skip the CanSeeSniper retreat block.
        ["AttackState_RetreatOnSniper_Disable"] = (
            signature:        "48 8B 07 48 8D 15 ? ? ? ? 48 8B 80 38 05 00 00 48 39 D0 0F 85 ? ? ? ? 80 BF B8 05 00 00 00 0F 84 50 03 00 00 4C 8D 35",
            patch:            "E9 51 03 00 00 90",
            expectedOriginal: "0F 84 50 03 00 00",
            patchOffset:      33
        ),

        // AttackState::OnUpdate: don't leave the nearby-fire threat path because spread is zero.
        ["AttackState_SkipSniperSpreadCheck"] = (
            signature:        "48 89 DF E8 ? ? ? ? F3 0F 10 8B E8 52 00 00 66 0F EF C0 0F 2F C8 0F 86 ? ? ? ?",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 86 ? ? ? ?",
            patchOffset:      23
        ),

        // Keep bot movement behavior when seeing enemies.
        ["AllSkill_KeepMoving_WhenSeeSniper"] = (
            signature:        "0F 2F 05 ? ? ? ? 76 0D 80 BB C4 05 00 00 00 0F 85",
            patch:            "90 90",
            expectedOriginal: "76 0D",
            patchOffset:      7
        ),

        ["AttackState_CanStrafe_jne"] = (
            signature:        "BE 01 00 00 00 48 89 DF E8 ? ? ? ? 84 C0 74 5F 80 BB A9 5C 00 00 00 0F 84",
            patch:            "90 90",
            expectedOriginal: "74 5F",
            patchOffset:      15
        ),

        // AttackState::OnEnter: force the reload-dodge chance flag true.
        ["AttackState_DodgeDuringReload"] = (
            signature:        "F3 0F 59 40 08 0F 2F C8 41 0F 97 44 24 44 48 81 C4 98 01 00 00",
            patch:            "41 C6 44 24 44 01",
            expectedOriginal: "41 0F 97 44 24 44",
            patchOffset:      8
        ),

        // AttackState::OnEnter: force the crouch-dodge chance flag true.
        ["SniperCrouchDodge_jb"] = (
            signature:        "0F 2F F8 66 0F EF C0 41 0F 93 44 24 42 E8 ? ? ? ? 48 8B 43 08",
            patch:            "41 C6 44 24 42 01",
            expectedOriginal: "41 0F 93 44 24 42",
            patchOffset:      7
        ),

        // AttackState::OnEnter: don't require the current weapon to be a sniper for dodge A.
        ["SniperDodge_SkipIsSniper_DodgeA"] = (
            signature:        "48 89 DF E8 ? ? ? ? 84 C0 0F 84 ? ? ? ? 44 8B 35 ? ? ? ? F3 0F 10 0D",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 84 ? ? ? ?",
            patchOffset:      10
        ),

        // Keep low-skill bots from delaying dodge transitions.
        ["LowSKill_JumpChance0"] = (
            signature:        "4C 0F 2F 05 ? ? ? ? 76 11",
            patch:            "EB 40",
            expectedOriginal: "76 11",
            patchOffset:      8
        ),

        // CCSBot::UpdateLookAround: ignore the movement timer gate.
        ["Vision_SkipIsMovingGate"] = (
            signature:        "F3 0F 10 83 00 06 00 00 0F 2F 05 ? ? ? ? 0F 87 ? ? ? ? 48 83 BB 58 55 00 00 00",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 87 ? ? ? ?",
            patchOffset:      15
        ),

        // Always take approach-body path in Vision logic.
        ["Vision_AlwaysEnterApproachBody"] = (
            signature:        "80 BB 39 04 00 00 00 0F 85 ? ? ? ? E9 ? ? ? ? F3 0F 10 8D 00 FF FF FF",
            patch:            "E9 A5 FD FF FF 90",
            expectedOriginal: "0F 85 ? ? ? ?",
            patchOffset:      7
        ),

        // CCSBot::UpdateLookAround: run the approach-point watch loop whenever present.
        ["Vision_AlwaysWatchApproachPoints"] = (
            signature:        "F3 0F 58 85 EC FE FF FF 80 BB F8 54 00 00 00 F3 0F 11 83 30 53 00 00 0F 84 ? ? ? ? F3 0F 10 1D",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 84 ? ? ? ?",
            patchOffset:      23
        ),

        // CCSBot::UpdateLookAround: skip the skill threshold before approach-body checks.
        ["Vision_ApproachBody_SkipSkillCheck"] = (
            signature:        "F3 0F 10 40 0C 0F 2F 05 ? ? ? ? 76 6D F3 0F 10 83 78 59 00 00",
            patch:            "90 90",
            expectedOriginal: "76 6D",
            patchOffset:      12
        ),

        // CCSBot::UpdateLookAround: don't leave the approach-body path when the hiding spot cone check fails.
        ["Vision_ApproachBody_SkipHidingSpotCheck"] = (
            signature:        "48 89 DF E8 ? ? ? ? 85 C0 74 29 48 8B 03 48 8D 15 ? ? ? ? 48 8B 80 B0 00 00 00",
            patch:            "90 90",
            expectedOriginal: "74 29",
            patchOffset:      10
        ),

        // Keep this entry as a Linux no-op for now. The Windows patch targets a noticable
        // helper, but current Linux builds inline that path into CCSBot::IsVisible(player).
        // Returning true from the function entry causes wall visibility; jumping over the
        // inline gate crashes when players connect on the current server build.
        ["IsNoticable_AlwaysTrue"] = (
            signature:        "55 48 8D 05 ? ? ? ? 48 89 E5 41 57 4C 8D 7D B0 41 56 41 89 D6 41 55 4C 89 FA",
            patch:            "55 48 8D",
            expectedOriginal: "55 48 8D",
            patchOffset:      0
        ),

        // CCSBot::InViewCone: do not reject targets outside the 60-degree outer FOV.
        ["InViewCone_RemoveOuterFOV"] = (
            signature:        "48 8B 47 18 48 8B 98 18 0D 00 00 48 8B 03 48 89 DF FF 90 D0 00 00 00 31 C0 0F 2F 05 ? ? ? ? 77 20",
            patch:            "90 90",
            expectedOriginal: "77 20",
            patchOffset:      32
        ),

        // CCSBot::InViewCone: treat all accepted targets as inside the inner cone.
        ["InViewCone_RemoveInnerFOV"] = (
            signature:        "FF 90 D0 00 00 00 B8 01 00 00 00 BA 02 00 00 00 0F 2F 05 ? ? ? ? 0F 46 C2 48 8B 5D F8 C9 C3",
            patch:            "89 D0 90",
            expectedOriginal: "0F 46 C2",
            patchOffset:      23
        ),

        // Keep InvestigateNoise always open in the same way as Windows.
        ["InvestigateNoise_SkipSelfDefenseCheck"] = (
            signature:        "83 BB ? ? 00 00 02 74 1E",
            patch:            "90 90",
            expectedOriginal: "74 1E",
            patchOffset:      7
        ),

        // CCSBot::OnAudibleEvent: accept sounds regardless of distance.
        ["OnAudibleEvent_GlobalHearRange"] = (
            signature:        "F3 0F 51 ED 0F 2F FD F3 0F 11 AD 10 FF FF FF 0F 86 ? ? ? ? 4C 89 EF",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 86 ? ? ? ?",
            patchOffset:      15
        ),

        // Idle/bomb-search fallback: GetNextBombsiteToSearch() -> GetPlantedBombsite().
        ["TBot_BombsiteSearch_UseKnownPlantedSite"] = (
            signature:        "48 8B BB 10 5E 00 00 E8 ? ? ? ? 4C 89 F7 E8 ? ? ? ? 49 8B 3C 24 31 F6",
            patch:            "E8 6C BF F6 FF",
            expectedOriginal: "E8 5C C2 F6 FF",
            patchOffset:      15
        ),

        // OnBombPickedUp: force the pathfind/hear gate to enter the tracking path.
        ["BombPickup_CT_GlobalHearRange"] = (
            signature:        "E8 ? ? ? ? 31 C9 BA 02 00 00 00 48 89 DF F3 0F 10 05 ? ? ? ? 48 89 C6 E8 ? ? ? ? 84 C0 75 84",
            patch:            "EB 84",
            expectedOriginal: "75 84",
            patchOffset:      33
        ),

        // OnBombBeep: ignore the 1500-unit hear range and update the bombsite from any distance.
        ["BombBeep_CT_GlobalHearRange"] = (
            signature:        "F3 0F 58 C2 F3 0F 58 C1 F3 0F 10 0D ? ? ? ? 0F 2F C8 0F 86 ? ? ? ? 48 8B 43 18",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 86 ? ? ? ?",
            patchOffset:      19
        ),

        // CSGameState::OnBombPlanted: all bot-owned game states learn the planted site.
        ["OnBombPlanted_AllBotsLearnSite"] = (
            signature:        "48 8B 83 08 51 00 00 48 8B 40 18 80 B8 24 06 00 00 02 0F 84 10 01 00 00 48 8B 7B 18",
            patch:            "E9 11 01 00 00 90",
            expectedOriginal: "0F 84 10 01 00 00",
            patchOffset:      18
        ),

        // CT defuse task path: SetDisposition(SELF_DEFENSE) -> ENGAGE_AND_INVESTIGATE.
        ["CT_Defuse_EngageAndInvestigate"] = (
            signature:        "48 8B 05 ? ? ? ? BE 02 00 00 00 48 89 DF 48 89 83 C8 05 00 00 E8 ? ? ? ? BA 02 00 00 00 4C 89 EE E9 ? ? ? ?",
            patch:            "BE 00 00 00 00",
            expectedOriginal: "BE 02 00 00 00",
            patchOffset:      7
        ),

        // DefuseBombState::OnUpdate: SetDisposition(SELF_DEFENSE) -> ENGAGE_AND_INVESTIGATE.
        ["DefuseBombState_OnUpdate_EngageAndInvestigate"] = (
            signature:        "55 48 8D BE 08 51 00 00 48 89 E5 41 54 53 48 89 F3 E8 ? ? ? ? BE 02 00 00 00 48 89 DF 49 89 C4 E8 ? ? ? ?",
            patch:            "BE 00 00 00 00",
            expectedOriginal: "BE 02 00 00 00",
            patchOffset:      22
        ),

        // DefuseBombState::OnEnter: SetDisposition(SELF_DEFENSE) -> ENGAGE_AND_INVESTIGATE.
        ["DefuseBombState_OnEnter_EngageAndInvestigate"] = (
            signature:        "55 48 89 E5 41 54 53 48 89 F3 BE 02 00 00 00 48 89 DF E8 ? ? ? ? 4C 8B A3 10 5E 00 00",
            patch:            "BE 00 00 00 00",
            expectedOriginal: "BE 02 00 00 00",
            patchOffset:      10
        ),

        // Disable flashbang avoidance SetLookAt/StopAiming block.
        ["FlashbangAvoidance_Disable"] = (
            signature:        "48 8D 35 ? ? ? ? 4C 89 E7 49 C7 84 24 68 53 00 00 00 00 00 00 0F 5C C2 F3 0F 11 4D A0 F3 0F 10 0D ? ? ? ? 0F 13 45 98 F3 0F 10 05 ? ? ? ? E8 ? ? ? ? 41 C6 84 24 5C 5C 00 00 00",
            patch:            "90 90 90 90 90 90 90 90 90 90 90 90 90 90",
            expectedOriginal: "E8 ? ? ? ? 41 C6 84 24 5C 5C 00 00 00",
            patchOffset:      50
        )
    };


    public override void Load(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches loading...");
        var patchDefinitions = _isLinux ? _patchDefinitionsLinux : _patchDefinitions;

        foreach (var name in patchDefinitions.Keys)
        {
            if (ApplyPatch(name, _isLinux)) Logger.LogInformation($"{name}: applied.");
            else                            Logger.LogError($"{name}: FAILED.");
        }

        RegisterEventHandler<EventPlayerSpawn>((@event, info) =>
        {
            var player = @event.Userid;
            if (player?.IsValid != true || !player.IsBot) return HookResult.Continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn?.IsValid != true
                || player.Team <= CsTeam.Spectator
                || !pawn.BotAllowActive)
                return HookResult.Continue;

            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null || gameRules.BombPlanted) return HookResult.Continue;

            UpdateBotBombState(pawn, player.PlayerName);
            return HookResult.Continue;
        });

        Logger.LogInformation($"Applied {_appliedPatches.Count}/{patchDefinitions.Count} patches.");
    }

    public override void Unload(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches unloading...");
        foreach (var patch in _appliedPatches) RestorePatch(patch);
        _appliedPatches.Clear();
        Logger.LogInformation("All patches restored.");
    }

    // ── Patch machinery ───────────────────────────────────────────────────────

    private bool ApplyPatch(string name, bool linux = false)
    {
        try
        {
            var patchDefinitions = linux ? _patchDefinitionsLinux : _patchDefinitions;
            if (!patchDefinitions.TryGetValue(name, out var def)) return false;

            nint sigAddr = NativeAPI.FindSignature(GameUtils.GetModulePath("server"), def.signature);
            if (sigAddr == 0) { Logger.LogError($"'{name}': signature not found."); return false; }

            nint addr     = sigAddr + def.patchOffset;
            var patchBytes = ParseHex(def.patch);
            if (patchBytes.Count == 0 || !IsValid(addr)) return false;

            var origBytes = new List<byte>();
            for (int i = 0; i < patchBytes.Count; i++)
                origBytes.Add(Marshal.ReadByte(addr, i));

            if (!ValidateOrig(name, origBytes, def.expectedOriginal))
            {
                Logger.LogError($"'{name}': byte mismatch. Expected [{def.expectedOriginal}] " +
                                $"got [{string.Join(" ", origBytes.Select(b => $"{b:X2}"))}].");
                return false;
            }

            if (!MemoryPatch.SetMemAccess(addr, patchBytes.Count)) return false;
            for (int i = 0; i < patchBytes.Count; i++) Marshal.WriteByte(addr, i, patchBytes[i]);

            _appliedPatches.Add(new PatchInfo(name, addr, origBytes));
            Logger.LogInformation($"'{name}' patched at 0x{addr:X} ({patchBytes.Count} bytes).");
            return true;
        }
        catch (Exception ex) { Logger.LogError($"'{name}': {ex.Message}"); return false; }
    }

    private void RestorePatch(PatchInfo p)
    {
        try
        {
            if (!IsValid(p.Address)) return;
            if (!MemoryPatch.SetMemAccess(p.Address, p.OriginalBytes.Count)) return;
            for (int i = 0; i < p.OriginalBytes.Count; i++)
                Marshal.WriteByte(p.Address, i, p.OriginalBytes[i]);
        }
        catch (Exception ex) { Logger.LogError($"Restore '{p.Name}': {ex.Message}"); }
    }

    private bool ValidateOrig(string name, List<byte> actual, string expectedHex)
    {
        try
        {
            var tokens = expectedHex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (actual.Count != tokens.Length) return false;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] == "?") continue;
                if (actual[i] != Convert.ToByte(tokens[i], 16)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool IsValid(nint addr)
    {
        if (addr == nint.Zero) return false;
        try { Marshal.ReadByte(addr); return true; }
        catch { return false; }
    }

    private static List<byte> ParseHex(string hex) =>
        [.. hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
               .Where(t => t != "?")
               .Select(t => Convert.ToByte(t, 16))];

    private bool UpdateBotBombState(CCSPlayerPawn pawn, string playerName)
    {
        try
        {
            if (pawn?.Bot?.Handle is not { } handle || handle == nint.Zero) return false;
            if (!IsValid(handle)) return false;

            nint gsPtr = handle + BotOffsets.m_gameState;
            if (!IsValid(gsPtr)) return false;
            if (Marshal.ReadByte(gsPtr + BotOffsets.m_isRoundOver) != 0) return true;

            nint bombAddr = gsPtr + BotOffsets.m_bombState;
            if (!IsValid(bombAddr)) return false;
            if (!MemoryPatch.SetMemAccess(bombAddr, sizeof(int))) return false;
            if (Marshal.ReadInt32(bombAddr) != 0) Marshal.WriteInt32(bombAddr, 0);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"UpdateBotBombState({playerName}): {ex.Message}");
            return false;
        }
    }
}
