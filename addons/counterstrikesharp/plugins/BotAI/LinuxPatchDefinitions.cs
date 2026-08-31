namespace BotAI;

internal static class LinuxPatchDefinitions
{
    internal static IReadOnlyDictionary<string, (string signature, string patch, string expectedOriginal, int patchOffset)> All { get; } =
        new Dictionary<string, (string signature, string patch, string expectedOriginal, int patchOffset)>()
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
            signature:        "C6 83 7C 4F 00 00 00 48 8B 5D F8 C9 E9 ? ? ? ?",
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
            signature:        "C6 83 54 4F 00 00 00 48 89 DF C6 83 7C 4F 00 00 00 E8 ? ? ? ? F3 0F 10 1D",
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
            signature:        "0F 2F C8 0F 86 ? ? ? ? 31 C9 31 D2 4C 89 E6 48 89 DF E8 ? ? ? ? 84 C0 0F 84 ? ? ? ? 48 83 C4 68 48 89 DF",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 84 ? ? ? ?",
            patchOffset:      26
        ),

        // Skip fire-rate check in AttackState::Update for low-latency patch.
        ["AttackState_SkipFireRateCheck"] = (
            signature:        "0F 2F 93 A0 07 00 00 0F 82 ? ? ? ?",
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
            signature:        "48 89 DF F3 0F 11 85 48 FE FF FF E8 ? ? ? ? 84 C0 0F 84 ? ? ? ? 44 8B 2D ? ? ? ? 45 89 EE",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 84 ? ? ? ?",
            patchOffset:      18
        ),

        // AttackState::OnUpdate: skip the CanSeeSniper retreat block.
        // 1.8.9: only the opcode byte is rewritten (je -> jmp); the rel8 displacement
        // stays whatever the binary shipped with, so it can never go stale.
        ["AttackState_RetreatOnSniper_Disable"] = (
            signature:        "48 8B 07 48 8D 15 ? ? ? ? 48 8B 80 38 05 00 00 48 39 D0 0F 85 ? ? ? ? 80 BF B8 05 00 00 00 74 73 4C 8D 35",
            patch:            "EB",
            expectedOriginal: "74",
            patchOffset:      33
        ),

        // AttackState::OnUpdate: don't leave the nearby-fire threat path because spread is zero.
        ["AttackState_SkipSniperSpreadCheck"] = (
            signature:        "F3 0F 10 8B E0 52 00 00 66 0F EF C0 0F 2F C8 0F 86 ? ? ? ?",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 86 ? ? ? ?",
            patchOffset:      15
        ),

        // Keep bot movement behavior when seeing enemies.
        ["AllSkill_KeepMoving_WhenSeeSniper"] = (
            signature:        "0F 2F 05 ? ? ? ? 76 0D 80 BB C4 05 00 00 00 0F 85",
            patch:            "90 90",
            expectedOriginal: "76 0D",
            patchOffset:      7
        ),

        ["AttackState_CanStrafe_jne"] = (
            signature:        "BE 01 00 00 00 48 89 DF E8 ? ? ? ? 84 C0 74 5D 80 BB A1 5C 00 00 00 0F 84",
            patch:            "90 90",
            expectedOriginal: "74 5D",
            patchOffset:      15
        ),

        // AttackState::OnEnter: force the reload-dodge chance flag true.
        ["AttackState_DodgeDuringReload"] = (
            signature:        "F3 0F 59 40 08 0F 2F C8 41 0F 97 44 24 44 48 81 C4 A8 01 00 00",
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
        // Deliberately still hardcoded: the new rel8 target (S+0x42) is a third
        // location ("non-jump path"), not the original branch target or fall-through,
        // so there is nothing at the site to derive it from. Needs its own anchor
        // signature to become drift-proof.
        ["LowSKill_JumpChance0"] = (
            signature:        "4C 0F 2F 05 ? ? ? ? 76 11",
            patch:            "EB 40",
            expectedOriginal: "76 11",
            patchOffset:      8
        ),

        // CCSBot::UpdateLookAround: ignore the movement timer gate.
        ["Vision_SkipIsMovingGate"] = (
            signature:        "F3 0F 10 83 00 06 00 00 66 0F EF C9 0F 2F C1 7A 08 74 06 0F 87 ? ? ? ?",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 87 ? ? ? ?",
            patchOffset:      19
        ),

        // 1.8.9: all `00 00 00 00` rel32 fields in the cave pairs below are COMPUTED at
        // load time from the resolved addresses of both pair members (see
        // LinuxDisplacementFixups). The cave<->site distance spans function boundaries
        // and changes on every game update — hardcoding it is what used to make a
        // sig-matched pair jump into the wrong bytes after build drift.
        ["Vision_AlwaysEnterApproachBody_Cave"] = (
            signature:        "48 89 DF FF D0 E9 ? ? ? ? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC",
            patch:            "48 83 7B 18 00 0F 84 00 00 00 00 E9 00 00 00 00",
            expectedOriginal: "CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC",
            patchOffset:      10
        ),

        // Always take approach-body path in Vision logic.
        ["Vision_AlwaysEnterApproachBody"] = (
            signature:        "80 BB 39 04 00 00 00 0F 85 ? ? ? ? E9 ? ? ? ? 66 0F 1F 44 00 00 48 89 DF",
            patch:            "E9 00 00 00 00 90",
            expectedOriginal: "0F 85 ? ? ? ?",
            patchOffset:      7
        ),

        ["Vision_AlwaysWatchApproachPoints_Cave"] = (
            signature:        "F3 0F 11 4D A8 E9 ? ? ? ? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC",
            patch:            "48 83 7B 18 00 0F 84 00 00 00 00 E9 00 00 00 00",
            expectedOriginal: "CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC",
            patchOffset:      10
        ),

        // CCSBot::UpdateLookAround: skip the skill threshold before approach-body checks.
        ["Vision_AlwaysWatchApproachPoints"] = (
            signature:        "F3 0F 58 85 EC FE FF FF 80 BB F0 54 00 00 00 F3 0F 11 83 28 53 00 00 0F 84 ? ? ? ? F3 0F 10 1D",
            patch:            "E9 00 00 00 00 90",
            expectedOriginal: "0F 84 ? ? ? ?",
            patchOffset:      23
        ),

        // Signature pins all 32 padding bytes the patch writes (was 30 — the last two
        // patch bytes were only covered by expectedOriginal, not the signature).
        ["Vision_AlwaysWatchApproachPoints_LoopEntry_Cave"] = (
            signature:        "48 8B 07 FF 50 20 E9 ? ? ? ? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC",
            patch:            "49 8B 7F 10 48 85 FF 0F 84 00 00 00 00 80 BA 24 06 00 00 02 75 05 BE 03 00 00 00 E9 00 00 00 00",
            expectedOriginal: "CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC",
            patchOffset:      11
        ),

        ["Vision_AlwaysWatchApproachPoints_LoopEntry"] = (
            signature:        "49 8B 56 18 BE 02 00 00 00 49 8B 7F 10 80 BA 24 06 00 00 02",
            patch:            "E9 00 00 00 00 90 90 90 90 90 90",
            expectedOriginal: "49 8B 7F 10 80 BA 24 06 00 00 02",
            patchOffset:      9
        ),

        // CCSBot::UpdateLookAround: don't leave the approach-body path when the hiding spot cone check fails.
        ["Vision_ApproachBody_SkipHidingSpotCheck"] = (
            signature:        "48 89 DF E8 ? ? ? ? 85 C0 0F 84 ? ? ? ? 48 8B 03 48 8D 15 ? ? ? ? 48 8B 80 B0 00 00 00",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 84 ? ? ? ?",
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
            signature:        "48 8B 47 18 48 8B 98 48 0D 00 00 48 8B 03 48 89 DF FF 90 E8 00 00 00 31 C0 0F 2F 05 ? ? ? ? 77 20",
            patch:            "90 90",
            expectedOriginal: "77 20",
            patchOffset:      32
        ),

        // CCSBot::InViewCone: treat all accepted targets as inside the inner cone.
        ["InViewCone_RemoveInnerFOV"] = (
            signature:        "FF 90 E8 00 00 00 B8 01 00 00 00 BA 02 00 00 00 0F 2F 05 ? ? ? ? 0F 46 C2 48 8B 5D F8 C9 C3",
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
            signature:        "F3 0F 51 ED 0F 2F E5 F3 0F 11 AD 2C FF FF FF 0F 86 ? ? ? ? 4C 89 EF",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 86 ? ? ? ?",
            patchOffset:      15
        ),

        // Idle/bomb-search fallback: GetNextBombsiteToSearch() -> GetPlantedBombsite().
        // Deliberately left hardcoded on BOTH sides (values from CS2-BotAI's 14172
        // revalidation): the call retargets one game function to a sibling function,
        // so there is nothing at the patch site to compute the new displacement from.
        // Hardcoding expectedOriginal keeps it fail-safe — on build drift validation
        // stops matching and the patch is skipped cleanly rather than writing a stale
        // call target. A future drift-proof version would resolve GetPlantedBombsite
        // via its own signature and compute the displacement like the cave pairs.
        ["TBot_BombsiteSearch_UseKnownPlantedSite"] = (
            signature:        "48 8B BB 08 5E 00 00 E8 ? ? ? ? 4C 89 F7 E8 ? ? ? ? 49 8B 3C 24 31 F6",
            patch:            "E8 4C 1D F6 FF",   // 14172: call GetPlantedBombsite
            expectedOriginal: "E8 3C 20 F6 FF",   // 14172: call GetNextBombsiteToSearch
            patchOffset:      15
        ),

        // OnBombPickedUp: force the pathfind/hear gate to enter the tracking path.
        // 1.8.9: opcode-only rewrite (jne -> jmp); rel8 displacement left untouched.
        ["BombPickup_CT_GlobalHearRange"] = (
            signature:        "E8 ? ? ? ? 31 C9 BA 02 00 00 00 48 89 DF F3 0F 10 05 ? ? ? ? 48 89 C6 E8 ? ? ? ? 84 C0 75 84",
            patch:            "EB",
            expectedOriginal: "75",
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
        // 1.8.9: rel32 computed at load as origRel32 + 1 (jz rel32 is 6 bytes, jmp rel32
        // is 5 — same target, instruction shrinks by one). expectedOriginal wildcards the
        // displacement so validation survives game updates.
        ["OnBombPlanted_AllBotsLearnSite"] = (
            signature:        "48 8B 83 00 51 00 00 48 8B 40 18 80 B8 24 06 00 00 02 0F 84 ? ? ? ? 48 8B 7B 18",
            patch:            "E9 00 00 00 00 90",
            expectedOriginal: "0F 84 ? ? ? ?",
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
            signature:        "55 48 8D BE 00 51 00 00 48 89 E5 41 54 53 48 89 F3 E8 ? ? ? ? BE 02 00 00 00 48 89 DF 49 89 C4 E8 ? ? ? ?",
            patch:            "BE 00 00 00 00",
            expectedOriginal: "BE 02 00 00 00",
            patchOffset:      22
        ),

        // DefuseBombState::OnEnter: SetDisposition(SELF_DEFENSE) -> ENGAGE_AND_INVESTIGATE.
        ["DefuseBombState_OnEnter_EngageAndInvestigate"] = (
            signature:        "55 48 89 E5 41 54 53 48 89 F3 BE 02 00 00 00 48 89 DF E8 ? ? ? ? 4C 8B A3 08 5E 00 00",
            patch:            "BE 00 00 00 00",
            expectedOriginal: "BE 02 00 00 00",
            patchOffset:      10
        ),

        // Disable flashbang avoidance SetLookAt/StopAiming block.
        ["FlashbangAvoidance_Disable"] = (
            signature:        "48 8D 35 ? ? ? ? 4C 89 E7 49 C7 84 24 60 53 00 00 00 00 00 00 0F 5C C2 F3 0F 11 4D A0 F3 0F 10 0D ? ? ? ? 0F 13 45 98 F3 0F 10 05 ? ? ? ? E8 ? ? ? ? 41 C6 84 24 54 5C 00 00 00",
            patch:            "90 90 90 90 90 90 90 90 90 90 90 90 90 90",
            expectedOriginal: "E8 ? ? ? ? 41 C6 84 24 54 5C 00 00 00",
            patchOffset:      50
        )
    };
}
