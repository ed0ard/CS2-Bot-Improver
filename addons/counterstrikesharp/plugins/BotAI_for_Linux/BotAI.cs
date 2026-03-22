using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using Common;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;

namespace BotAI;

public record PatchInfo(string Name, nint Address, List<byte> OriginalBytes);

public static class BotOffsets
{
    public const int m_gameState = 0x6038;         // CSGameState* in CCSBot (24632)
    public const int m_isRoundOver = 0x08;         // bool in CSGameState (8)
    public const int m_bombState = 0x0C;           // BombState in CSGameState (12)
}

[MinimumApiVersion(304)]
public class BotAI : BasePlugin
{
    public override string ModuleName => "Patches - Bot AI";
    public override string ModuleVersion => "1.3";
    public override string ModuleAuthor => "K4ryuu";
    public override string ModuleDescription => "Prevents bots from visiting enemy spawn at round start";

    private readonly List<PatchInfo> _appliedPatches = [];

    private readonly Dictionary<string, (string signature, string patch, string expectedOriginal)> _patchDefinitions = new()
    {
        ["HasVisitedEnemySpawn"] = (
            signature: "40 88 B7 05 05 00 00",          // mov BYTE PTR [rdi+0x505], sil
            patch: "C6 87 05 05 00 00 01",              // mov BYTE PTR [rdi+0x505], 0x1
            expectedOriginal: "40 88 B7 05 05 00 00"    // Expected original bytes
        ),
        ["GameState_Reset"] = (
            signature: "44 89 77 ? F3",                 // mov [rdi+?], r14d
            patch: "0F 1F 40 00",                       // 4-byte NOP
            expectedOriginal: "44 89 77 0C"             // Expected original bytes (without wildcard)
        ),
        ["Idle_IsSafeAlwaysFalse"] = (
            signature: "74 28 33 D2 48 8B CE E8 ? ? ? ? 84 C0 75 1A",
            patch: "EB 28",                             // JZ short +28  ->  JMP short +28
            expectedOriginal: "74 28"                   // Check that there is actually a JZ at the address
        ),
        ["EscapeFromFlames_OnEnter_NoEquipKnife"] = (
            signature: "E8 ? ? ? ? F3 0F 10 46 ? 0F 2E C7 48 89 7E ? 7A ? 74 ? BA ? ? ? ? 48 8D 4E ? 41 B8 ? ? ? ? E8 ? ? ? ? F3 0F 11 7E ? F3 0F 10 46 ? 0F 2E C6 7A ? 74 ? BA ? ? ? ? 48 8D 4E ? 41 B8 ? ? ? ? E8 ? ? ? ? C7 46 ? ? ? ? ? 48 8B 5C 24 ? 48 8B 74 24 ? 0F 28 74 24 ? 0F 28 7C 24 ? 44 0F 28 44 24 ? 48 83 C4 ? 5F C3 CC CC CC CC CC CC CC CC 40 53",
            patch: "90 90 90 90 90",
            expectedOriginal: "E8 ? ? ? ?"
        )
    };

    public override void Load(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches loading...");

        foreach (var patchName in _patchDefinitions.Keys)
        {
            if (ApplyPatch(patchName))
            {
                Logger.LogInformation($"{patchName} patch applied successfully!");
            }
            else
            {
                Logger.LogError($"Failed to apply {patchName} patch!");
            }
        }

        RegisterEventHandler<EventPlayerSpawn>((@event, info) =>
        {
            var player = @event.Userid;
            if (player?.IsValid != true || !player.IsBot)
                return HookResult.Continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn?.IsValid != true || player.Team <= CsTeam.Spectator || !pawn.BotAllowActive)
                return HookResult.Continue;

            var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
            if (gameRules == null || gameRules.BombPlanted)
                return HookResult.Continue;

            UpdateBotBombState(pawn, player.PlayerName);
            return HookResult.Continue;
        });

        Logger.LogInformation($"Applied {_appliedPatches.Count}/{_patchDefinitions.Count} patches.");
    }

    private bool UpdateBotBombState(CCSPlayerPawn pawn, string playerName)
    {
        try
        {
            if (pawn?.Bot?.Handle == null || pawn.Bot.Handle == nint.Zero)
                return false;

            nint botPtr = pawn.Bot.Handle;
            if (!IsValidMemoryAddress(botPtr))
                return false;

            nint gameStatePtr = botPtr + BotOffsets.m_gameState;
            if (!IsValidMemoryAddress(gameStatePtr))
                return false;

            bool isRoundOver = Marshal.ReadByte(gameStatePtr + BotOffsets.m_isRoundOver) != 0;
            if (isRoundOver)
                return true;

            nint bombStateAddr = gameStatePtr + BotOffsets.m_bombState;
            if (!IsValidMemoryAddress(bombStateAddr))
                return false;

            if (!MemoryPatch.SetMemAccess(bombStateAddr, sizeof(int)))
                return false;

            int currentBombState = Marshal.ReadInt32(bombStateAddr);
            if (currentBombState != 0)
            {
                Marshal.WriteInt32(bombStateAddr, 0);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to update bot bomb state for {playerName}: {ex.Message}");
            return false;
        }
    }

    private static bool IsValidMemoryAddress(nint address)
    {
        if (address == nint.Zero)
            return false;

        try
        {
            Marshal.ReadByte(address);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public override void Unload(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches unloading...");

        foreach (var patch in _appliedPatches)
        {
            RestorePatch(patch);
        }

        _appliedPatches.Clear();
        Logger.LogInformation("All patches restored.");
    }

    private bool ApplyPatch(string name)
    {
        try
        {
            if (!_patchDefinitions.TryGetValue(name, out var patchDef))
                return false;

            string modulePath = GameUtils.GetModulePath("server");
            nint address = NativeAPI.FindSignature(modulePath, patchDef.signature);
            if (address == 0)
                return false;

            var patchBytes = ParseHexString(patchDef.patch);
            if (patchBytes.Count == 0 || !IsValidMemoryAddress(address))
                return false;

            var originalBytes = new List<byte>();
            for (int i = 0; i < patchBytes.Count; i++)
            {
                originalBytes.Add(Marshal.ReadByte(address, i));
            }

            // Validate original bytes match expected pattern
            if (!ValidateOriginalBytes(name, originalBytes, patchDef.expectedOriginal))
            {
                Logger.LogError($"Original bytes validation failed for patch '{name}' - refusing to patch");
                return false;
            }

            if (!MemoryPatch.SetMemAccess(address, patchBytes.Count))
                return false;

            for (int i = 0; i < patchBytes.Count; i++)
            {
                Marshal.WriteByte(address, i, patchBytes[i]);
            }

            _appliedPatches.Add(new PatchInfo(name, address, originalBytes));
            Logger.LogInformation($"Patch '{name}' applied at 0x{address:X} ({patchBytes.Count} bytes)");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to apply patch '{name}': {ex.Message}");
            return false;
        }
    }

    private void RestorePatch(PatchInfo patch)
    {
        try
        {
            if (!IsValidMemoryAddress(patch.Address))
                return;

            if (!MemoryPatch.SetMemAccess(patch.Address, patch.OriginalBytes.Count))
                return;

            for (int i = 0; i < patch.OriginalBytes.Count; i++)
            {
                Marshal.WriteByte(patch.Address, i, patch.OriginalBytes[i]);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to restore patch '{patch.Name}': {ex.Message}");
        }
    }

    private bool ValidateOriginalBytes(string patchName, List<byte> actualBytes, string expectedHex)
    {
        try
        {
            var expectedTokens = expectedHex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (actualBytes.Count != expectedTokens.Length)
            {
                Logger.LogWarning($"Patch '{patchName}': byte count mismatch - expected {expectedTokens.Length}, got {actualBytes.Count}");
                return false;
            }

            for (int i = 0; i < expectedTokens.Length; i++)
            {
                if (expectedTokens[i] == "?")
                    continue; // wildcard → skip check

                byte expectedByte = Convert.ToByte(expectedTokens[i], 16);
                if (actualBytes[i] != expectedByte)
                {
                    Logger.LogWarning($"Patch '{patchName}': byte mismatch at offset {i} - expected {expectedByte:X2}, got {actualBytes[i]:X2}");
                    return false;
                }
            }

            Logger.LogInformation($"Patch '{patchName}': original bytes validated successfully");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Patch '{patchName}': validation error - {ex.Message}");
            return false;
        }
    }

    private static List<byte> ParseHexString(string hexString)
    {
        return [.. hexString.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(hex => Convert.ToByte(hex, 16))];
    }
}