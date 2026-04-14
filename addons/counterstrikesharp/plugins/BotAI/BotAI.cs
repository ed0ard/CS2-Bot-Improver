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
    public override string ModuleVersion     => "1.6.6";
    public override string ModuleAuthor      => "K4ryuu & Austin (updated by ed0ard)";
    public override string ModuleDescription =>
        "Improve and fix bots' behavior comprehensively";

    private readonly List<PatchInfo> _appliedPatches = [];

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


        ["InvestigateNoise_SkipSelfDefenseCheck"] = (
            signature:        "83 BB 08 63 00 00 02 74 1E",
            patch:            "90 90",
            expectedOriginal: "74 1E",
            patchOffset:      7    // RVA 0x318ed6
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


        ["AttackState_SkipSteadyFireShortcut"] = (
            signature:        "0F B6 F0 84 C0 74 3C 48 8B 4B 18 48 8B 11 FF 92 90 00 00 00",
            patch:            "90 90",
            expectedOriginal: "74 3C",
            patchOffset:      5    // RVA 0x2f1be5: je+3C → NOP (remove HasViewBeenSteady fire shortcut)
        ),
 
        ["AttackState_SkipZoomFireShortcut"] = (
            signature:        "FF 90 A0 02 00 00 84 C0 74 15 48 8D 8B 88 00 00 00 48 89 AB",
            patch:            "90 90",
            expectedOriginal: "74 15",
            patchOffset:      8    // RVA 0x2f1c0c: je+15 → NOP (remove IsWaitingForZoom fire shortcut)
        ),

        ["AttackState_SkipSniperSpreadCheck"] = (
            signature:        "41 0F 28 C8 0F 57 C0 FF 15 ? ? ? ? F3 0F 10 0D ? ? ? ? 0F 2F C8 0F 86",
            patch:            "90 90 90 90 90 90",
            expectedOriginal: "0F 86 7B 04 00 00",
            patchOffset:      24   // RVA 0x320153: NOP jbe+47B
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

        ["AttackState_SprayRangeExtend"] = (
            signature:        "44 0F 2F 0D ? ? ? ? 76 0A F3 0F 10 3D ? ? ? ? EB 08 F3 0F 10 3D",
            patch:            "9F 6C 24 01",
            expectedOriginal: "C5 01 25 01",
            patchOffset:      14   // RVA 0x31dc55: movss xmm7
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
            signature:        "0F 2F 05 75 E4 23 01 76 11",
            patch:            "EB 40",
            expectedOriginal: "76 11",
            patchOffset:      7    // RVA 0x2f4587: jbe +11 → jmp +40 to non-jump 
        ),

        ["AllSkill_DodgeChance100_OnOutnumberedOrSniper"] = (
            signature:        "0F 28 F0 F3 0F 59 35 60 C4 22 01 76 14",
            patch:            "90 90",
            expectedOriginal: "76 14",
            patchOffset:      11   // RVA 0x319d73: jbe +14 → NOP
        ),

        ["DodgeChance_Flat80"] = (
            signature:        "0F 28 F0 F3 0F 59 35 60 C4 22 01 76 14",
            patch:            "10",
            expectedOriginal: "59",
            patchOffset:      5    // RVA 0x319d66: MULSS(59) → MOVSS(10)
        ),

        ["AllSkill_KeepMoving_WhenSeeSniper"] = (
            signature:        "0F 2F 05 AF 6E 26 01 76 0D 80 BF AC 05 00 00 00 0F 85",
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


        ["Vision_AlwaysWatchApproachPoints"] = (
            signature:        "80 BF B1 6C 00 00 00 75 25 0F 2F",
            patch:            "EB 25",
            expectedOriginal: "75 25",
            patchOffset:      7    // VA 0x180319304: jne→jmp
        ),

        ["Vision_ApproachBody_SkipSkillCheck"] = (
            signature:        "0F 2F C6 76 33 80 BF B1 6C 00 00 00 74 2A",
            patch:            "90 90",
            expectedOriginal: "76 33",
            patchOffset:      3
        ),

        ["Vision_ApproachBody_SkipHidingSpotCheck"] = (
            signature:        "0F 2F C6 76 33 80 BF B1 6C 00 00 00 74 2A",
            patch:            "90 90",
            expectedOriginal: "74 2A",
            patchOffset:      12
        ),

        ["Vision_SkipIsMovingGate"] = (
            signature:        "0F 2F 3D ? ? ? ? 77 0F 49 8B D6 48 8B CF E8",
            patch:            "90 90",
            expectedOriginal: "77 0F",//RVA 0x319306: ja → NOP
            patchOffset:      7
        ),

        ["Vision_AlwaysEnterApproachBody"] = (
            signature:        "84 C0 75 0D 48 C7 45 08 00 00 00 00 E9",
            patch:            "EB 0D",
            expectedOriginal: "75 0D",//RVA 0x31931c: jne → jmp
            patchOffset:      2
        ),
    };

    public override void Load(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches loading...");

        foreach (var name in _patchDefinitions.Keys)
        {
            if (ApplyPatch(name)) Logger.LogInformation($"{name}: applied.");
            else                  Logger.LogError($"{name}: FAILED.");
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

        Logger.LogInformation($"Applied {_appliedPatches.Count}/{_patchDefinitions.Count} patches.");
    }

    public override void Unload(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches unloading...");
        foreach (var patch in _appliedPatches) RestorePatch(patch);
        _appliedPatches.Clear();
        Logger.LogInformation("All patches restored.");
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
