using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using Common;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace BotAI;

public record PatchInfo(string Name, nint Address, List<byte> OriginalBytes);

public static class BotOffsets
{
    public const int m_gameState   = 0x6038;   // CSGameState* in CCSBot
    public const int m_isRoundOver = 0x08;     // bool in CSGameState
    public const int m_bombState   = 0x0C;     // BombState in CSGameState
}

[MinimumApiVersion(304)]
public class BotAI : BasePlugin
{
    public override string ModuleName        => "Patches - Bot AI";
    public override string ModuleVersion     => "1.4.1";
    public override string ModuleAuthor      => "Austin(updated by ed0ard)";
    public override string ModuleDescription => "Prevents bots from visiting enemy spawn at round start";

    private readonly List<PatchInfo> _appliedPatches = [];

    private readonly Dictionary<string, (string signature, string patch, string expectedOriginal, int patchOffset)> _patchDefinitions = new()
    {
        ["HasVisitedEnemySpawn"] = (
            // mov BYTE PTR [rdi+0x520], sil  (offset changed from 0x505 to 0x520)
            signature:        "40 88 B7 20 05 00 00",
            patch:            "C6 87 20 05 00 00 01",
            expectedOriginal: "40 88 B7 20 05 00 00",
            patchOffset:      0
        ),

        ["GameState_Reset"] = (
            // cmp [rdi+C], 0 / je +7 / mov [rdi+C], 0  — patch the write at offset +6
            signature:        "83 7F 0C 00 74 07 C7 47 0C 00 00 00 00",
            patch:            "0F 1F 80 00 00 00 00",   // 7-byte NOP
            expectedOriginal: "C7 47 0C 00 00 00 00",
            patchOffset:      6
        ),

        ["Idle_IsSafeAlwaysFalse"] = (
            signature:        "74 28 33 D2 48 8B CE E8 ? ? ? ? 84 C0 75 1A",
            patch:            "EB 28",
            expectedOriginal: "74 28",
            patchOffset:      0
        ),
    };

    public override void Load(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches loading...");

        foreach (var patchName in _patchDefinitions.Keys)
        {
            if (ApplyPatch(patchName))
                Logger.LogInformation($"{patchName} patch applied successfully!");
            else
                Logger.LogError($"Failed to apply {patchName} patch!");
        }

        try
        {
            VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Hook(OnBotWeaponCanAcquire, HookMode.Pre);
            Logger.LogInformation("EscapeFromFlames_NoKnife: CanAcquire hook registered.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"EscapeFromFlames_NoKnife: failed to register CanAcquire hook: {ex.Message}");
        }

        RegisterEventHandler<EventPlayerSpawn>((@event, info) =>
        {
            var player = @event.Userid;
            if (player?.IsValid != true || !player.IsBot)
                return HookResult.Continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn?.IsValid != true || player.Team <= CsTeam.Spectator || !pawn.BotAllowActive)
                return HookResult.Continue;

            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null || gameRules.BombPlanted)
                return HookResult.Continue;

            UpdateBotBombState(pawn, player.PlayerName);
            return HookResult.Continue;
        });

        Logger.LogInformation($"Applied {_appliedPatches.Count}/{_patchDefinitions.Count} patches.");
    }

    public override void Unload(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches unloading...");

        try
        {
            VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Unhook(OnBotWeaponCanAcquire, HookMode.Pre);
        }
        catch { /* hook may not have been registered */ }

        foreach (var patch in _appliedPatches)
            RestorePatch(patch);

        _appliedPatches.Clear();
        Logger.LogInformation("All patches restored.");
    }


    private static bool IsKnifeDefIndex(uint defIndex)
        => defIndex == 42 || defIndex == 59 || (defIndex >= 500 && defIndex <= 599);

    private HookResult OnBotWeaponCanAcquire(DynamicHook hook)
    {
        try
        {
            // ── 1. Only act on live bots ──────────────────────────────
            var itemServices = hook.GetParam<CCSPlayer_ItemServices>(0);
            if (itemServices == null) return HookResult.Continue;

            var pawn = itemServices.Pawn.Value;
            if (pawn == null || !pawn.IsValid) return HookResult.Continue;

            var controller = pawn.Controller.Value?.As<CCSPlayerController>();
            if (controller == null || !controller.IsValid || !controller.IsBot || !controller.PawnIsAlive)
                return HookResult.Continue;

            // ── 2. Identify the weapon ────────────────────────────────
            var itemView = hook.GetParam<CEconItemView>(1);
            if (itemView == null) return HookResult.Continue;

            bool isKnife;
            var vdata = VirtualFunctions.GetCSWeaponDataFromKeyFunc
                            .Invoke(-1, itemView.ItemDefinitionIndex.ToString());

            if (vdata != null)
            {
                // Primary: correct direction – does the weapon name contain "weapon_knife"?
                isKnife = vdata.Name.Contains("weapon_knife");
            }
            else
            {
                // Fallback: GetCSWeaponDataFromKeyFunc unavailable (gamedata sig drift).
                isKnife = IsKnifeDefIndex(itemView.ItemDefinitionIndex);
            }

            if (!isKnife) return HookResult.Continue;

            // ── 3. Block the knife ────────────────────────────────────
            hook.SetReturn(AcquireResult.InvalidItem);
            return HookResult.Stop;
        }
        catch
        {
            return HookResult.Continue;
        }
    }


    private bool ApplyPatch(string name)
    {
        try
        {
            if (!_patchDefinitions.TryGetValue(name, out var def))
                return false;

            string modulePath = GameUtils.GetModulePath("server");
            nint sigAddress = NativeAPI.FindSignature(modulePath, def.signature);
            if (sigAddress == 0)
            {
                Logger.LogError($"Patch '{name}': signature not found in server.dll");
                return false;
            }

            // Apply the optional offset to reach the actual patch target
            nint address = sigAddress + def.patchOffset;

            var patchBytes = ParseHexString(def.patch);
            if (patchBytes.Count == 0 || !IsValidMemoryAddress(address))
                return false;

            // Read current bytes for validation and later restoration
            var originalBytes = new List<byte>();
            for (int i = 0; i < patchBytes.Count; i++)
                originalBytes.Add(Marshal.ReadByte(address, i));

            if (!ValidateOriginalBytes(name, originalBytes, def.expectedOriginal))
            {
                var actual = string.Join(" ", originalBytes.Select(b => $"{b:X2}"));
                Logger.LogError($"Patch '{name}': byte mismatch at target. Expected: [{def.expectedOriginal}]  Got: [{actual}]");
                Logger.LogError($"Patch '{name}': server.dll may have been updated again – signature needs refresh");
                return false;
            }

            if (!MemoryPatch.SetMemAccess(address, patchBytes.Count))
                return false;

            for (int i = 0; i < patchBytes.Count; i++)
                Marshal.WriteByte(address, i, patchBytes[i]);

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
            if (!IsValidMemoryAddress(patch.Address)) return;
            if (!MemoryPatch.SetMemAccess(patch.Address, patch.OriginalBytes.Count)) return;

            for (int i = 0; i < patch.OriginalBytes.Count; i++)
                Marshal.WriteByte(patch.Address, i, patch.OriginalBytes[i]);
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
            var tokens = expectedHex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (actualBytes.Count != tokens.Length)
            {
                Logger.LogWarning($"Patch '{patchName}': byte count mismatch – expected {tokens.Length}, got {actualBytes.Count}");
                return false;
            }

            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] == "?") continue;
                byte expected = Convert.ToByte(tokens[i], 16);
                if (actualBytes[i] != expected)
                {
                    Logger.LogWarning($"Patch '{patchName}': byte[{i}] mismatch – expected 0x{expected:X2}, got 0x{actualBytes[i]:X2}");
                    return false;
                }
            }

            Logger.LogInformation($"Patch '{patchName}': original bytes validated successfully");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Patch '{patchName}': validation error – {ex.Message}");
            return false;
        }
    }

    private static bool IsValidMemoryAddress(nint address)
    {
        if (address == nint.Zero) return false;
        try { Marshal.ReadByte(address); return true; }
        catch { return false; }
    }

    private static List<byte> ParseHexString(string hexString) =>
        [.. hexString.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                     .Where(t => t != "?")
                     .Select(t => Convert.ToByte(t, 16))];


    private bool UpdateBotBombState(CCSPlayerPawn pawn, string playerName)
    {
        try
        {
            if (pawn?.Bot?.Handle == null || pawn.Bot.Handle == nint.Zero)
                return false;

            nint botPtr = pawn.Bot.Handle;
            if (!IsValidMemoryAddress(botPtr)) return false;

            nint gameStatePtr = botPtr + BotOffsets.m_gameState;
            if (!IsValidMemoryAddress(gameStatePtr)) return false;

            bool isRoundOver = Marshal.ReadByte(gameStatePtr + BotOffsets.m_isRoundOver) != 0;
            if (isRoundOver) return true;

            nint bombStateAddr = gameStatePtr + BotOffsets.m_bombState;
            if (!IsValidMemoryAddress(bombStateAddr)) return false;

            if (!MemoryPatch.SetMemAccess(bombStateAddr, sizeof(int))) return false;

            if (Marshal.ReadInt32(bombStateAddr) != 0)
                Marshal.WriteInt32(bombStateAddr, 0);

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to update bot bomb state for {playerName}: {ex.Message}");
            return false;
        }
    }
}
