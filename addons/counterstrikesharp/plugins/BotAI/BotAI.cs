using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
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
    public override string ModuleVersion     => "1.4.2";
    public override string ModuleAuthor      => "Austin (updated by ed0ard)";
    public override string ModuleDescription => "Improve bots' behavior";

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

        // NOP the BombState reset to avoid bot standing still
        ["GameState_Reset"] = (
            signature:        "83 7F 0C 00 74 07 C7 47 0C 00 00 00 00",
            patch:            "0F 1F 80 00 00 00 00",
            expectedOriginal: "C7 47 0C 00 00 00 00",
            patchOffset:      6
        ),

        // IsSafe() always false in IdleState
        ["Idle_IsSafeAlwaysFalse"] = (
            signature:        "74 28 33 D2 48 8B CE E8 ? ? ? ? 84 C0 75 1A",
            patch:            "EB 28",
            expectedOriginal: "74 28",
            patchOffset:      0
        ),


        // EscapeFromBombState::OnEnter tail-call jmp → ret
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
