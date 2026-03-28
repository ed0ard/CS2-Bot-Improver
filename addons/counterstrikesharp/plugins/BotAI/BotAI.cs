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
    public override string ModuleVersion     => "1.6.1";
    public override string ModuleAuthor      => "Austin (updated by ed0ard)";
    public override string ModuleDescription =>
        "Improve and fix bots' behavior comprehensively";

    private readonly List<PatchInfo> _appliedPatches = [];

    private bool _check1cActive = false;

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
            signature:        "48 83 C4 20 5B E9 BB 50 F9 FF",
            patch:            "C3 90 90 90 90",
            expectedOriginal: "E9 BB 50 F9 FF",
            patchOffset:      5
        ),

        // EscapeFromBombState::OnUpdate call → NOP
        ["EscapeFromBomb_OnUpdate_NoEquipKnife"] = (
            signature:        "75 0F 48 8B 5C 24 50 48 83 C4 40 5F E9 ? ? ? ? E8 E8 24 F9 FF",
            patch:            "90 90 90 90 90",
            expectedOriginal: "E8 E8 24 F9 FF",
            patchOffset:      17
        ),

        // EscapeFromFlamesState::OnEnter call → NOP
        ["EscapeFromFlames_OnEnter_NoEquipKnife"] = (
            signature:        "40 88 BB 64 5F 00 00 40 88 BB 8C 5F 00 00 E8 D1 4F F9 FF",
            patch:            "90 90 90 90 90",
            expectedOriginal: "E8 D1 4F F9 FF",
            patchOffset:      14
        ),


        // Remove total buy-attempt cap (was 5, raised to 127)
        ["BotBuy_RemoveAttemptLimit"] = (
            signature:        "83 F9 05 0F 8E 8E 02 00 00",
            patch:            "83 F9 7F",
            expectedOriginal: "83 F9 05",
            patchOffset:      0
        ),

        ["Check_1C_SkipSavingMoneyFlag"] = (
            signature:        "80 79 18 00 74 62 48 8B",
            patch:            "EB 62",
            expectedOriginal: "74 62",
            patchOffset:      4
        ),


        ["InvestigateNoise_SkipSelfDefenseCheck"] = (
            signature:        "83 BB 08 63 00 00 02 74 1E",
            patch:            "90 90",
            expectedOriginal: "74 1E",
            patchOffset:      7    // RVA 0x318ed6
        ),

        ["InvestigateNoise_SkipRecentEnemyCheck"] = (
            signature:        "84 C0 74 33 48 8B CB",
            patch:            "90 90",
            expectedOriginal: "74 33",
            patchOffset:      2    // RVA 0x318ec1
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


        ["SprayAllDistances_FireDecision1"] = (
            signature:        "F3 0F 10 87 A0 00 00 00 0F 2F C7 76 12 48 8B 05",
            patch:            "90 90",
            expectedOriginal: "76 12",
            patchOffset:      11    // RVA 0x2f0d53
        ),

        ["SprayAllDistances_FireDecision2"] = (
            signature:        "0F 2F 40 30 76 05 40 B5 01 EB 03 40 32 ED",
            patch:            "90 90",
            expectedOriginal: "76 05",
            patchOffset:      4    // RVA 0x2f0d60
        ),

        ["AttackState_SkipFireRateCheck"] = (
            signature:        "0F 2F 8B AC 00 00 00 0F 82",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 82 87 00 00 00",
            patchOffset:      7    // VA 0x1802f22a0
        ),

        ["AttackSkipViewSteadyCheck"] = (
            signature:        "0F 2F C1 0F 86 94 00 00 00 0F 2F 8B",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 86 94 00 00 00",
            patchOffset:      3    // RVA 0x2f2293
        ),

        ["SprayAllDistances_ja1"] = (
            signature:        "0F 2F F9 F3 44 0F 10 15 ? ? ? ? 77 22",
            patch:            "90 90",
            expectedOriginal: "77 22",
            patchOffset:      12
        ),

        ["SprayAllDistances_ja2"] = (
            signature:        "F3 0F 10 4C AB 7C 44 0F 28 C8 0F 2F F9 77 21",
            patch:            "90 90",
            expectedOriginal: "77 21",
            patchOffset:      13
        ),

        ["SprayAllDistances_ja3"] = (
            signature:        "0F 2F FA 77 17 48 8B CF E8",
            patch:            "90 90",
            expectedOriginal: "77 17",
            patchOffset:      3
        ),

        ["SprayAllDistances_ja4"] = (
            signature:        "0F 10 94 AB 80 00 00 00 0F 2F FA 77 13 48",
            patch:            "90 90",
            expectedOriginal: "77 13",
            patchOffset:      11
        ),


        ["AttackState_DodgeDuringReload"] = (
            signature:        "E9 ? ? ? ? 0F 2F BB A4 00 00 00 76 74",
            patch:            "EB 74",
            expectedOriginal: "76 74",
            patchOffset:      12    // BLOCK_TIMER_A jbe→jmp
        ),

        ["SniperCrouchDodge_jb"] = (
            signature:        "0F 2F BB A4 00 00 00 0F 28 7C 24 30 76 74",
            patch:            "90 90",
            expectedOriginal: "76 74",
            patchOffset:      12    // BLOCK_TIMER_B NOP jbe → DODGE_B (RVA 0x2f2420)
        ),

        ["LowSKill_JumpChance0"] = (
            signature:        "0F 2F 05 65 D5 23 01 76 11",
            patch:            "EB 40",
            expectedOriginal: "76 11",
            patchOffset:      7    // RVA 0x2f4587: jbe +11 → jmp +40 to non-jump 
        ),

        // NOP the JLE that guards the sharp-turn escape block
        ["StuckCheck_SkipEscapeDirection"] = (
            signature:    "0F 8E 96 00 00 00 E8 5A AC B5 00 48 85 C0",
            patch:        "90 90 90 90 90 90",
            expectedOriginal: "0F 8E 96 00 00 00",
            patchOffset:  0    // RVA 0x2d21b7
        ),

        ["AllSkill_DodgeChance100_OnOutnumberedOrSniper"] = (
            signature:        "0F 28 F0 F3 0F 59 35 30 B5 22 01 76 14",
            patch:            "90 90",
            expectedOriginal: "76 14",
            patchOffset:      11   // RVA 0x319d73: jbe +14 → NOP
        ),

        ["DodgeChance_Flat80"] = (
            signature:        "0F 28 F0 F3 0F 59 35 30 B5 22 01 76 14",
            patch:            "10",
            expectedOriginal: "59",
            patchOffset:      5    // RVA 0x319d66: MULSS(59) → MOVSS(10)
        ),

        ["AllSkill_KeepMoving_WhenSeeSniper"] = (
            signature:        "0F 2F 05 9F 5F 26 01 76 0D 80 BF AC 05 00 00 00 0F 85",
            patch:            "90 90",
            expectedOriginal: "76 0D",
            patchOffset:      7    // RVA 0x2cbb4d: jbe +0D → NOP
        ),

        ["AttackState_CanStrafe_jne"] = (
            signature:        "E8 B2 1C 00 00 84 C0 74 7B",
            patch:            "90 90",
            expectedOriginal: "74 7B",
            patchOffset:      7    // RVA 0x2f22b0
        ),

        ["SniperDodge_SkipIsSniper_DodgeA"] = (
            signature:        "84 F6 75 6A 48 8B 05",
            patch:            "90 90",
            expectedOriginal: "75 6A",
            patchOffset:      2    // RVA 0x2f23a8：DODGE_A IsSniper jne+6A → NOP
        ),
    };

    private const string Check1cName = "Check_1C_SkipSavingMoneyFlag";

    public override void Load(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches loading...");

        foreach (var name in _patchDefinitions.Keys)
        {
            if (name == Check1cName) continue;
            if (ApplyPatch(name)) Logger.LogInformation($"{name}: applied.");
            else                  Logger.LogError($"{name}: FAILED.");
        }

        RegisterEventHandler<EventRoundStart>((@event, info) =>
        {
            ConVar? botLoadout = ConVar.Find("bot_loadout");
            if (botLoadout != null && !string.IsNullOrEmpty(botLoadout.StringValue))
            {
                if (_check1cActive)
                    RemoveCheck1cPatch();

                return HookResult.Continue;
            }
            
            if (IsRestrictedGameMode())
            {
                Logger.LogInformation($"[Check_1C] Current game mode is restricted, skip loading patch.");
                if (_check1cActive)
                {
                    RemoveCheck1cPatch();
                }
                return HookResult.Continue;
            }

            if (IsFirstRoundOfHalf())
            {
                if (!_check1cActive)
                {
                    if (ApplyPatch(Check1cName))
                    {
                        _check1cActive = true;
                        Logger.LogInformation($"{Check1cName}: applied (first round of half).");
                    }
                    else
                    {
                        Logger.LogError($"{Check1cName}: FAILED to apply on first round of half.");
                    }
                }
            }
            else
            {
                if (_check1cActive)
                {
                    RemoveCheck1cPatch();
                    Logger.LogInformation($"{Check1cName}: not first round of half, skipped/removed.");
                }
            }
            return HookResult.Continue;
        });

        RegisterEventHandler<EventRoundFreezeEnd>((@event, info) =>
        {
            if (_check1cActive)
            {
                RemoveCheck1cPatch();
                Logger.LogInformation($"{Check1cName}: freezetime ended, patch removed.");
            }
            return HookResult.Continue;
        });

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

        Logger.LogInformation($"Applied {_appliedPatches.Count}/{_patchDefinitions.Count - 1} persistent patches (Check_1C managed dynamically).");
    }

    public override void Unload(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches unloading...");

        foreach (var patch in _appliedPatches) RestorePatch(patch);
        _appliedPatches.Clear();
        _check1cActive = false;
        Logger.LogInformation("All patches restored.");
    }

    private bool IsRestrictedGameMode()
    {
        int gameType = ConVar.Find("game_type")?.GetPrimitiveValue<int>() ?? 0;
        int gameMode = ConVar.Find("game_mode")?.GetPrimitiveValue<int>() ?? 0;

        bool isDeathmatch = (gameType == 1 && gameMode == 2);
        bool isArmsRace   = (gameType == 1 && gameMode == 0);

        return isDeathmatch || isArmsRace;
    }

    private bool IsFirstRoundOfHalf()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null) return false;

            int played      = gameRules.TotalRoundsPlayed;
            int maxRounds   = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            int otMaxRounds = ConVar.Find("mp_overtime_maxrounds")?.GetPrimitiveValue<int>() ?? 6;

            if (maxRounds   <= 0) maxRounds   = 24;
            if (otMaxRounds <= 0) otMaxRounds = 6;

            int halfLength   = maxRounds   / 2;
            int otHalfLength = otMaxRounds / 2;

            if (played == 0 || played == halfLength)
            {
                Logger.LogInformation($"[Check_1C] Regular match first round of half detected (played={played}).");
                return true;
            }

            if (played >= maxRounds)
            {
                int otPlayed = played - maxRounds;
                int posInOT  = otPlayed % otMaxRounds;

                if (posInOT == 0 || posInOT == otHalfLength)
                {
                    Logger.LogInformation($"[Check_1C] Overtime first round of half detected (played={played}, posInOT={posInOT}).");
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError($"IsFirstRoundOfHalf: {ex.Message}");
            return false;
        }
    }

    private void RemoveCheck1cPatch()
    {
        var patch = _appliedPatches.FirstOrDefault(p => p.Name == Check1cName);
        if (patch == null) return;

        RestorePatch(patch);
        _appliedPatches.Remove(patch);
        _check1cActive = false;
    }

    // ── Patch machinery ───────────────────────────────────────────────────────

    private bool ApplyPatch(string name)
    {
        try
        {
            if (!_patchDefinitions.TryGetValue(name, out var def)) return false;

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
